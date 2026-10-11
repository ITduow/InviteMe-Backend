using System.Text;
using ClosedXML.Excel;
using InviteMe.Application.Common.Errors;
using InviteMe.Infrastructure.Persistence.Adapters;

namespace InviteMe.UnitTests;

public sealed class GuestImportFileTests
{
    private static byte[] Csv(params string[] rows) => Encoding.UTF8.GetBytes(string.Join(",", GuestImportFileCodec.Headers) + "\r\n" + string.Join("\r\n", rows));

    [Fact]
    public void CsvPreservesVietnameseLeadingZeroAndMapsLabels()
    {
        var (rows, errors) = GuestImportFileCodec.Parse("guests.csv", Csv("Nguyễn Minh Anh,0912345678,anh@example.com,Nhà gái,Bạn bè,Bạn đại học,1,1,\"Ghi chú, có dấu\""));
        var row = Assert.Single(rows); Assert.Empty(errors);
        Assert.Equal("Nguyễn Minh Anh", row.FullName); Assert.Equal("0912345678", row.Phone);
        Assert.Equal("BRIDE", row.Side); Assert.Equal("FRIEND", row.Relationship); Assert.Equal("Ghi chú, có dấu", row.Notes);
    }

    [Fact]
    public void InvalidFieldsHaveSpecificRowAndColumnAndAreNotCoercedToOther()
    {
        var (rows, errors) = GuestImportFileCodec.Parse("guests.csv", Csv("A,,bad-email,Nhà gáii,Bạn thân,,-1,1.5,"));
        Assert.Equal(5, errors.Count); Assert.All(errors, x => Assert.Equal(2, x.RowNumber));
        Assert.Null(Assert.Single(rows).Side); Assert.Null(rows[0].Relationship);
        Assert.Contains(errors, x => x.Column == "Phía khách mời");
    }

    [Fact]
    public void BlankClassificationsRemainUnknownAndCountsDefaultToZero()
    {
        var (rows, errors) = GuestImportFileCodec.Parse("guests.csv", Csv("A,,,,,,,,"));
        Assert.Empty(errors); var row = Assert.Single(rows);
        Assert.Null(row.Side); Assert.Null(row.Relationship); Assert.Equal(0, row.MaxPlusOne);
    }

    [Fact]
    public void XlsxUploadPreservesPhoneAndMapsClassifications()
    {
        using var workbook = CreateUploadWorkbook();
        var sheet = workbook.Worksheet("KhachMoi");
        sheet.Cell("A2").Value = "Trần Thị Lan"; sheet.Cell("B2").Value = "0901234567";
        sheet.Cell("D2").Value = "Nhà trai"; sheet.Cell("E2").Value = "Họ hàng";
        using var stream = new MemoryStream(); workbook.SaveAs(stream);
        var (rows, errors) = GuestImportFileCodec.Parse("guests.xlsx", stream.ToArray());
        Assert.Empty(errors); var row = Assert.Single(rows);
        Assert.Equal("0901234567", row.Phone); Assert.Equal("GROOM", row.Side); Assert.Equal("FAMILY", row.Relationship);
    }

    [Fact]
    public void FormulaCellsAreRejected()
    {
        using var workbook = CreateUploadWorkbook();
        workbook.Worksheet("KhachMoi").Cell("A2").FormulaA1 = "1+1";
        using var stream = new MemoryStream(); workbook.SaveAs(stream);
        Assert.Equal("IMPORT_FORMULA", Assert.Throws<ApplicationProblemException>(() => GuestImportFileCodec.Parse("a.xlsx", stream.ToArray())).Code);
    }

    [Fact]
    public void HeadersAndSizeAndFormatAreValidated()
    {
        Assert.Equal("IMPORT_HEADERS", Assert.Throws<ApplicationProblemException>(() => GuestImportFileCodec.Parse("a.csv", Encoding.UTF8.GetBytes("Name\r\nA"))).Code);
        Assert.Equal("IMPORT_SIZE", Assert.Throws<ApplicationProblemException>(() => GuestImportFileCodec.Parse("a.csv", new byte[GuestImportFileCodec.MaxBytes + 1])).Code);
        Assert.Equal("IMPORT_FORMAT", Assert.Throws<ApplicationProblemException>(() => GuestImportFileCodec.Parse("a.xls", [1])).Code);
        Assert.Equal("IMPORT_ROWS", Assert.Throws<ApplicationProblemException>(() => GuestImportFileCodec.Parse("a.csv", Csv(Enumerable.Repeat("A,,,,,,,,", 501).ToArray()))).Code);
    }

    [Fact]
    public void DuplicateDetectionRequiresNameAndNormalizesVietnamesePhones()
    {
        var row = Assert.Single(GuestImportFileCodec.Parse("a.csv", Csv("Nguyễn An,+84912345678,,,,,,," )).Rows);
        Assert.True(GuestImportFileCodec.Duplicate(row, "nguyễn an", "0912345678", null));
        Assert.False(GuestImportFileCodec.Duplicate(row, "Nguyễn Bình", "0912345678", null));
    }

    private static XLWorkbook CreateUploadWorkbook()
    {
        var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("KhachMoi");
        for (var i = 0; i < GuestImportFileCodec.Headers.Length; i++)
            sheet.Cell(1, i + 1).Value = GuestImportFileCodec.Headers[i];
        return workbook;
    }
}
