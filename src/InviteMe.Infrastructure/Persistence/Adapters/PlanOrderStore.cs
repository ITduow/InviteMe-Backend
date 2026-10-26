using System.Globalization;
using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Billing;
using InviteMe.Application.Features.Gifts;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Billing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace InviteMe.Infrastructure.Persistence.Adapters;

/// <summary>
/// Plan purchases. An order and the wedding row are locked while money is applied, so a webhook retry, a second transfer
/// and an admin approval cannot activate twice. Activation replaces the wedding's ACTIVE subscription.
/// </summary>
public sealed class PlanOrderStore(InviteMeDbContext db, TimeProvider clock, BillingAccountOptions account, IWeddingEntitlements entitlements)
    : IPlanOrderStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Vietnam = TimeSpan.FromHours(7);

    // ---------- owner ----------

    public async Task<PlanOrderResult> CreateAsync(Guid weddingId, string planCode, Guid actor, CancellationToken ct)
    {
        if (!account.Configured)
            throw new ApplicationProblemException(ProblemKind.Unavailable, "BILLING_NOT_CONFIGURED", "Thanh toán gói đang tạm đóng.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(weddingId, ct);
        var plan = await db.Set<Plan>().AsNoTracking().SingleOrDefaultAsync(x => x.Code == planCode && x.Status == Plan.Active, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "PLAN_NOT_FOUND", "Gói không tồn tại hoặc đã ngừng bán.");
        if (plan.Code == Plan.Free) throw Rule("PLAN_NOT_FOR_SALE", "Gói miễn phí không cần mua.");
        var current = await entitlements.GetAsync(weddingId, ct);
        if (current.Legacy)
            throw Rule("PLAN_NOT_NEEDED", "Đám cưới này đã có đầy đủ tính năng, không cần mua gói.");
        var currentPlan = await db.Set<Plan>().AsNoTracking().Where(x => x.Code == current.PlanCode).Select(x => new { x.Id, x.Price }).SingleAsync(ct);
        // An upgrade only costs the difference with the plan already in force.
        var amount = (long)(plan.Price - currentPlan.Price);
        if (amount <= 0)
            throw Rule(plan.Code == current.PlanCode ? "PLAN_ALREADY_ACTIVE" : "PLAN_DOWNGRADE_NOT_ALLOWED",
                plan.Code == current.PlanCode ? "Đám cưới đang dùng gói này." : "Chỉ có thể nâng lên gói cao hơn.");

        var now = clock.GetUtcNow();
        var open = await db.Set<PlanOrder>().Where(x => x.WeddingId == weddingId && (x.Status == PlanOrder.Pending || x.Status == PlanOrder.Underpaid))
            .ToListAsync(ct);
        if (open.FirstOrDefault(x => x.PaidAmount > 0) is { } partly)
            throw new ApplicationProblemException(ProblemKind.Conflict, "PLAN_ORDER_UNDERPAID",
                $"Đơn {partly.Code} đã nhận một phần tiền. Chuyển nốt phần còn thiếu hoặc liên hệ InviteMe.");
        // Same plan and price while the code is still valid: show the same QR again.
        if (open.FirstOrDefault(x => x.PlanId == plan.Id && x.Amount == amount && now <= x.ExpiresAt) is { } same)
            return new(Dto(same, plan), false);
        foreach (var old in open) { old.Cancel(actor, "Thay bằng đơn mới", now); db.AuditLogs.Add(Audit(old, "PLAN_ORDER_REPLACED", actor)); }

        var order = PlanOrder.Create(weddingId, actor, plan.Id, PlanOrderRules.NewCode(), amount, now, currentPlan.Id);
        db.Add(order);
        db.AuditLogs.Add(Audit(order, "PLAN_ORDER_CREATED", actor));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(Dto(order, plan), true);
    }

    public async Task<IReadOnlyList<PlanOrderDto>> ListAsync(Guid weddingId, CancellationToken ct)
    {
        var rows = await (from o in db.Set<PlanOrder>().AsNoTracking()
                          join p in db.Set<Plan>().AsNoTracking() on o.PlanId equals p.Id
                          where o.WeddingId == weddingId
                          orderby o.CreatedAt descending
                          select new { o, p }).ToListAsync(ct);
        return rows.Select(x => Dto(x.o, x.p)).ToList();
    }

    public async Task<PlanOrderDto> GetAsync(Guid weddingId, Guid orderId, CancellationToken ct) =>
        await (from o in db.Set<PlanOrder>().AsNoTracking()
               join p in db.Set<Plan>().AsNoTracking() on o.PlanId equals p.Id
               where o.Id == orderId && o.WeddingId == weddingId
               select new { o, p }).SingleOrDefaultAsync(ct) is { } row ? Dto(row.o, row.p) : throw Missing("PLAN_ORDER_NOT_FOUND");

    public async Task<PlanOrderDto> CancelAsync(Guid weddingId, Guid orderId, Guid actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LockOrder(orderId, ct);
        if (order is null || order.WeddingId != weddingId) throw Missing("PLAN_ORDER_NOT_FOUND");
        if (order.PaidAmount > 0 || !order.Open)
            throw Rule("PLAN_ORDER_CLOSED", order.PaidAmount > 0 ? "Đơn đã nhận tiền, liên hệ InviteMe để huỷ." : "Đơn đã hoàn tất hoặc đã huỷ.");
        order.Cancel(actor, null, clock.GetUtcNow());
        db.AuditLogs.Add(Audit(order, "PLAN_ORDER_CANCELLED", actor));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return await GetAsync(weddingId, orderId, ct);
    }

    // ---------- webhook ----------

    public async Task<PlanPaymentResult> ProcessPaymentAsync(SePayWebhookPayload payload, string code, string rawJson, CancellationToken ct)
    {
        if (await Replay(payload.Id, ct) is { } replay) return replay;
        var now = clock.GetUtcNow();
        var accountNumber = account.Owns(payload.SubAccount) ? payload.SubAccount! : payload.AccountNumber ?? "";
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await db.Set<PlanOrder>().FromSqlInterpolated($"SELECT * FROM inviteme.plan_orders WHERE code = {code} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        var status = PlanPayment.Review;
        string? reason;
        var activated = false;
        if (order is null) reason = PlanOrderRules.UnknownCode;
        else if (!account.Owns(accountNumber)) reason = PlanOrderRules.WrongAccount; // the money is not in InviteMe's account
        else if (!order.Open) reason = PlanOrderRules.OrderClosed;                    // paid twice, or paid a cancelled order
        else if (order.Status == PlanOrder.Pending && now > order.ExpiresAt) reason = PlanOrderRules.OrderExpired; // QR older than 24 h
        else if (!await PriceStillValid(order, ct)) reason = PlanOrderRules.PriceChanged; // plan edited, granted or changed since the order
        else
        {
            var transactionAt = GiftRules.ParseSePayDate(payload.TransactionDate, now);
            activated = order.AddPayment(payload.TransferAmount, transactionAt);
            if (activated) await Activate(order, null, now, ct);
            status = PlanPayment.Applied; reason = null;
            db.AuditLogs.Add(Audit(order, activated ? "PLAN_ORDER_PAID" : "PLAN_ORDER_UNDERPAID", null, payload.TransferAmount));
        }
        var payment = PlanPayment.Create(payload.Id, order?.Id, code, payload.TransferAmount, Cut(accountNumber, 30), Cut(payload.Content, 500),
            payload.ReferenceCode is null ? null : Cut(payload.ReferenceCode, 100), GiftRules.ParseSePayDate(payload.TransactionDate, now), status,
            reason, rawJson, now);
        db.Add(payment);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "uq_plan_payments_provider_id" })
        {
            await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
            return await Replay(payload.Id, ct) ?? throw new InvalidOperationException("Plan payment replay not found.");
        }
        return new(status, reason, order?.Id, order?.WeddingId, activated, false);
    }

    // ---------- admin ----------

    public async Task<IReadOnlyList<AdminPlanDto>> AdminPlansAsync(CancellationToken ct)
    {
        var counts = await db.Set<Subscription>().AsNoTracking().Where(x => x.WeddingId != null && x.Status == Subscription.Active)
            .GroupBy(x => x.PlanId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return (await db.Set<Plan>().AsNoTracking().OrderBy(x => x.Price).ToListAsync(ct))
            .Select(p => AdminDto(p, counts.GetValueOrDefault(p.Id))).ToList();
    }

    public async Task<AdminPlanDto> UpdatePlanAsync(string code, UpdatePlan input, Guid actor, CancellationToken ct)
    {
        var plan = await db.Set<Plan>().SingleOrDefaultAsync(x => x.Code == code, ct) ?? throw Missing("PLAN_NOT_FOUND");
        if (code == Plan.Free && (input.Price != 0 || input.Status != Plan.Active))
            throw Rule("PLAN_FREE_LOCKED", "Gói miễn phí phải giữ giá 0đ và luôn mở (đám cưới mới bắt đầu từ gói này).");
        var before = new { plan.Name, plan.Price, plan.Status, plan.LimitsJson };
        plan.Update(input.Name.Trim(), input.Price, input.Limits.ToJson(), input.Status);
        // Open orders were priced with the old price: owners order again (orders that already got money wait for an admin).
        if (before.Price != plan.Price || before.Status != plan.Status)
            await CancelUnpaidOrders(x => x.PlanId == plan.Id || x.BasePlanId == plan.Id, actor, "Giá hoặc trạng thái gói đã đổi", ct);
        db.AuditLogs.Add(AuditLog.ForPlatform(actor, "plans", plan.Id, "PLAN_UPDATED",
            JsonSerializer.Serialize(new { code, before, after = new { plan.Name, plan.Price, plan.Status, plan.LimitsJson } }, Json)));
        await db.SaveChangesAsync(ct);
        var active = await db.Set<Subscription>().CountAsync(x => x.PlanId == plan.Id && x.WeddingId != null && x.Status == Subscription.Active, ct);
        return AdminDto(plan, active);
    }

    public async Task<PagedResult<AdminPlanOrderDto>> AdminOrdersAsync(string? status, string? search, PageRequest page, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var query = AdminOrders();
        if (status == PlanOrder.Expired) query = query.Where(x => x.Order.Status == PlanOrder.Pending && x.Order.ExpiresAt < now);
        else if (status == PlanOrder.Pending) query = query.Where(x => x.Order.Status == PlanOrder.Pending && x.Order.ExpiresAt >= now);
        else if (status is not null) query = query.Where(x => x.Order.Status == status);
        if (search is not null)
        {
            var like = "%" + search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(x => EF.Functions.ILike(x.Order.Code, like) || (x.Title != null && EF.Functions.ILike(x.Title, like))
                                     || (x.Email != null && EF.Functions.ILike(x.Email, like)));
        }
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.Order.CreatedAt).ThenBy(x => x.Order.Id)
            .Skip((page.Page - 1) * page.PageSize).Take(page.PageSize).ToListAsync(ct);
        return new(rows.Select(AdminDto).ToList(), page.Page, page.PageSize, total);
    }

    public async Task<PagedResult<AdminPlanPaymentDto>> AdminPaymentsAsync(string? status, PageRequest page, CancellationToken ct)
    {
        var query = db.Set<PlanPayment>().AsNoTracking();
        if (status is not null) query = query.Where(x => x.Status == status);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.ReceivedAt).ThenBy(x => x.Id).Skip((page.Page - 1) * page.PageSize).Take(page.PageSize).ToListAsync(ct);
        return new(rows.Select(p => new AdminPlanPaymentDto(p.Id, p.ProviderTransactionId, p.OrderId, p.Code, p.Amount, p.AccountNumber, p.Content,
            p.ReferenceCode, p.Status, p.Reason, p.TransactionAt, p.ReceivedAt)).ToList(), page.Page, page.PageSize, total);
    }

    public async Task<AdminPlanOrderDto> ApproveOrderAsync(Guid orderId, string? note, Guid actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LockOrder(orderId, ct) ?? throw Missing("PLAN_ORDER_NOT_FOUND");
        if (order.Status == PlanOrder.Paid) throw Rule("PLAN_ORDER_CLOSED", "Đơn đã được thanh toán.");
        if (order.WeddingId is { } weddingId) await LockWedding(weddingId, ct, requireOpen: false);
        var now = clock.GetUtcNow();
        order.Approve(actor, note, now);
        await Activate(order, actor, now, ct);
        db.AuditLogs.Add(Audit(order, "PLAN_ORDER_APPROVED", actor));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return await AdminOrder(orderId, ct);
    }

    public async Task<AdminPlanOrderDto> CancelOrderAsync(Guid orderId, string? note, Guid actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LockOrder(orderId, ct) ?? throw Missing("PLAN_ORDER_NOT_FOUND");
        if (!order.Open) throw Rule("PLAN_ORDER_CLOSED", "Đơn đã hoàn tất hoặc đã huỷ.");
        order.Cancel(actor, note, clock.GetUtcNow());
        db.AuditLogs.Add(Audit(order, "PLAN_ORDER_CANCELLED", actor));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return await AdminOrder(orderId, ct);
    }

    public async Task<WeddingPlanDto> GrantAsync(Guid weddingId, GrantPlan input, Guid actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wedding = await LockWedding(weddingId, ct, requireOpen: false);
        var plan = await db.Set<Plan>().AsNoTracking().SingleOrDefaultAsync(x => x.Code == input.PlanCode, ct) ?? throw Missing("PLAN_NOT_FOUND");
        foreach (var old in await db.Set<Subscription>().Where(x => x.WeddingId == weddingId && x.Status == Subscription.Active).ToListAsync(ct))
            old.Cancel();
        await CancelUnpaidOrders(x => x.WeddingId == weddingId, actor, "Admin đã đặt gói cho đám cưới", ct);
        await db.SaveChangesAsync(ct); // the unique "one ACTIVE per wedding" index is checked row by row
        db.Add(Subscription.ForWedding(wedding.OwnerUserId, plan.Id, weddingId, clock.GetUtcNow()));
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "subscriptions", weddingId, "PLAN_GRANTED", actor, null,
            JsonSerializer.Serialize(new { plan = plan.Code, note = input.Note }, Json)));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return await entitlements.GetAsync(weddingId, ct);
    }

    public async Task<PlanRevenueDto> RevenueAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var rows = await (from pay in db.Set<PlanPayment>().AsNoTracking()
                          join o in db.Set<PlanOrder>().AsNoTracking() on pay.OrderId equals (Guid?)o.Id
                          join p in db.Set<Plan>().AsNoTracking() on o.PlanId equals p.Id
                          where pay.Status == PlanPayment.Applied && (@from == null || pay.TransactionAt >= @from) && (to == null || pay.TransactionAt <= to)
                          select new { pay.Amount, pay.TransactionAt, p.Code, OrderId = o.Id, o.Status }).ToListAsync(ct);
        var review = await db.Set<PlanPayment>().CountAsync(x => x.Status == PlanPayment.Review, ct);
        PlanRevenueRow Row(IGrouping<string, (long Amount, Guid OrderId)> g) => new(g.Key, g.Sum(x => x.Amount), g.Select(x => x.OrderId).Distinct().Count());
        return new(from, to, rows.Sum(x => x.Amount), rows.Where(x => x.Status == PlanOrder.Paid).Select(x => x.OrderId).Distinct().Count(),
            rows.GroupBy(x => x.Code, x => (x.Amount, x.OrderId)).Select(Row).OrderByDescending(x => x.Amount).ToList(),
            rows.GroupBy(x => x.TransactionAt.ToOffset(Vietnam).ToString("yyyy-MM", CultureInfo.InvariantCulture), x => (x.Amount, x.OrderId))
                .Select(Row).OrderBy(x => x.Key).ToList(), review);
    }

    // ---------- helpers ----------

    /// <summary>
    /// The amount was computed from the plan in force and the target price at order time. Money only activates the plan when
    /// both still hold (locks the wedding so a parallel grant cannot slip in); otherwise an admin decides.
    /// </summary>
    private async Task<bool> PriceStillValid(PlanOrder order, CancellationToken ct)
    {
        if (order.WeddingId is not { } weddingId) return false;
        await LockWedding(weddingId, ct, requireOpen: false);
        var target = await db.Set<Plan>().AsNoTracking().SingleAsync(x => x.Id == order.PlanId, ct);
        if (target.Status != Plan.Active) return false;
        var current = await db.Set<Subscription>().AsNoTracking().Where(x => x.WeddingId == weddingId && x.Status == Subscription.Active)
            .Select(x => (Guid?)x.PlanId).SingleOrDefaultAsync(ct);
        if (current != order.BasePlanId) return false;
        var basePrice = order.BasePlanId is { } baseId
            ? await db.Set<Plan>().AsNoTracking().Where(x => x.Id == baseId).Select(x => x.Price).SingleAsync(ct) : 0m;
        return (long)(target.Price - basePrice) == order.Amount;
    }

    private async Task CancelUnpaidOrders(System.Linq.Expressions.Expression<Func<PlanOrder, bool>> which, Guid actor, string note, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        foreach (var order in await db.Set<PlanOrder>().Where(x => x.Status == PlanOrder.Pending && x.PaidAmount == 0).Where(which).ToListAsync(ct))
        {
            order.Cancel(actor, note, now);
            db.AuditLogs.Add(Audit(order, "PLAN_ORDER_CANCELLED", actor));
        }
    }

    /// <summary>The paid plan replaces whatever plan the wedding had (an upgrade from FREE or from a smaller plan).</summary>
    private async Task Activate(PlanOrder order, Guid? actor, DateTimeOffset now, CancellationToken ct)
    {
        if (order.WeddingId is not { } weddingId) return; // wedding deleted meanwhile: the money record stays
        var owner = await db.Weddings.Where(x => x.Id == weddingId).Select(x => x.OwnerUserId).SingleAsync(ct);
        foreach (var old in await db.Set<Subscription>().Where(x => x.WeddingId == weddingId && x.Status == Subscription.Active).ToListAsync(ct))
            old.Cancel();
        await db.SaveChangesAsync(ct);
        db.Add(Subscription.ForWedding(owner, order.PlanId, weddingId, now).PaidBy(order.Id));
        db.AuditLogs.Add(Audit(order, "PLAN_ACTIVATED", actor));
    }

    private async Task<PlanPaymentResult?> Replay(long providerId, CancellationToken ct) =>
        await (from pay in db.Set<PlanPayment>().AsNoTracking()
               join o in db.Set<PlanOrder>().AsNoTracking() on pay.OrderId equals (Guid?)o.Id into orders
               from o in orders.DefaultIfEmpty()
               where pay.ProviderTransactionId == providerId
               select new PlanPaymentResult(pay.Status, pay.Reason, pay.OrderId, o == null ? null : o.WeddingId, false, true)).SingleOrDefaultAsync(ct);

    private PlanOrderDto Dto(PlanOrder o, Plan p)
    {
        var bank = GiftBanks.Find(account.BankBin);
        var due = o.Amount - o.PaidAmount;
        var status = o.DisplayStatus(clock.GetUtcNow());
        return new(o.Id, o.WeddingId, p.Code, p.Name, o.Code, o.Amount, o.PaidAmount, status, account.BankBin ?? "", bank?.Name ?? "",
            account.AccountNumber?.Trim() ?? "", GiftRules.Ascii(account.AccountName ?? ""),
            o.Open && due > 0 && account.Configured ? GiftRules.QrImageUrl(account.BankBin!, account.AccountNumber!.Trim(), account.AccountName!, due, o.Code) : null,
            o.CreatedAt, o.ExpiresAt, o.PaidAt);
    }

    private sealed class AdminRow
    {
        public required PlanOrder Order { get; init; }
        public required string PlanCode { get; init; }
        public string? Title { get; init; }
        public string? Email { get; init; }
    }

    private IQueryable<AdminRow> AdminOrders() =>
        from o in db.Set<PlanOrder>().AsNoTracking()
        join p in db.Set<Plan>().AsNoTracking() on o.PlanId equals p.Id
        join w in db.Weddings.AsNoTracking() on o.WeddingId equals (Guid?)w.Id into weddings
        from w in weddings.DefaultIfEmpty()
        join u in db.Users.AsNoTracking() on o.UserId equals (Guid?)u.Id into users
        from u in users.DefaultIfEmpty()
        select new AdminRow { Order = o, PlanCode = p.Code, Title = w == null ? null : w.Title, Email = u == null ? null : u.Email };

    private async Task<AdminPlanOrderDto> AdminOrder(Guid orderId, CancellationToken ct) =>
        AdminDto(await AdminOrders().SingleAsync(x => x.Order.Id == orderId, ct));

    private AdminPlanOrderDto AdminDto(AdminRow x) => new(x.Order.Id, x.Order.WeddingId, x.Title, x.Email, x.PlanCode, x.Order.Code,
        x.Order.Amount, x.Order.PaidAmount, x.Order.DisplayStatus(clock.GetUtcNow()), x.Order.Note, x.Order.CreatedAt, x.Order.ExpiresAt,
        x.Order.PaidAt, x.Order.CancelledAt);

    private static AdminPlanDto AdminDto(Plan p, int activeWeddings) =>
        new(p.Code, p.Name, p.Price, p.Currency, p.Status, PlanLimits.Parse(p.LimitsJson), activeWeddings);

    private Task<PlanOrder?> LockOrder(Guid orderId, CancellationToken ct) =>
        db.Set<PlanOrder>().FromSqlInterpolated($"SELECT * FROM inviteme.plan_orders WHERE id = {orderId} FOR UPDATE").SingleOrDefaultAsync(ct);

    // A closed wedding can still finish paying or be granted a plan; only new orders need an open wedding.
    private async Task<Domain.Weddings.Wedding> LockWedding(Guid weddingId, CancellationToken ct, bool requireOpen = true)
    {
        var wedding = await db.Weddings.FromSqlInterpolated($"SELECT * FROM inviteme.weddings WHERE id = {weddingId} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw Missing("WEDDING_NOT_FOUND");
        if (requireOpen && !wedding.CanEdit) throw Rule("WEDDING_CLOSED", "Wedding is closed.");
        return wedding;
    }

    private static AuditLog Audit(PlanOrder order, string action, Guid? actor, long? transferAmount = null) =>
        order.WeddingId is { } weddingId
            ? AuditLog.ForEntity(weddingId, "plan_orders", order.Id, action, actor, null, Metadata(order, transferAmount))
            : AuditLog.ForPlatform(actor ?? Guid.Empty, "plan_orders", order.Id, action, Metadata(order, transferAmount));

    private static string Metadata(PlanOrder order, long? transferAmount) =>
        JsonSerializer.Serialize(new { order.Code, order.Amount, order.PaidAmount, order.Status, transferAmount }, Json);

    private static string Cut(string? text, int max) => (text ?? "").Length <= max ? text ?? "" : text![..max];
    private static ApplicationProblemException Missing(string code) => new(ProblemKind.NotFound, code, "Record was not found.");
    private static ApplicationProblemException Rule(string code, string message) => new(ProblemKind.BusinessRule, code, message);
}
