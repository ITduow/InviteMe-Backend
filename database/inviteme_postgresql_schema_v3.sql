-- ============================================================
-- InviteMe PostgreSQL schema v3 — consolidated fresh-install script
-- ============================================================
-- v3 = schema v2 (inviteme_postgresql_schema_v2.sql / BaselineV2.sql)
--      + IdentitySupport + WorkspaceSupport + InvitationLifecycleSupport
--      + SeatingCheckInWorkflows + RSVP-02 RsvpImmutableHistory.
--
-- Generated with pg_dump --schema-only from a database built by applying exactly those
-- repository SQL resources in migration order (PostgreSQL 16), then seed rows appended.
--
-- Use ONLY for a brand-new empty database. Existing databases are upgraded through EF
-- migrations (dotnet ef database update); see docs/database.md and docs/RSVP-02_DB_Audit.md.
-- A database created from this file has no __EFMigrationsHistory rows; prefer
-- `dotnet ef database update` on an empty database when the API will manage migrations.
-- ============================================================

BEGIN;
CREATE EXTENSION IF NOT EXISTS pgcrypto WITH SCHEMA public;
CREATE EXTENSION IF NOT EXISTS unaccent WITH SCHEMA public;
--
-- PostgreSQL database dump
--


-- Dumped from database version 16.15 (Ubuntu 16.15-0ubuntu0.24.04.1)
-- Dumped by pg_dump version 16.15 (Ubuntu 16.15-0ubuntu0.24.04.1)

SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

--
-- Name: inviteme; Type: SCHEMA; Schema: -; Owner: -
--

CREATE SCHEMA inviteme;


--
-- Name: rsvp_history_append_only(); Type: FUNCTION; Schema: inviteme; Owner: -
--

CREATE FUNCTION inviteme.rsvp_history_append_only() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'rsvp_history is append-only: % is not allowed', TG_OP
        USING ERRCODE = 'restrict_violation',
              HINT = 'Record a new RSVP change instead of editing history.';
END;
$$;


--
-- Name: set_table_activated_at(); Type: FUNCTION; Schema: inviteme; Owner: -
--

CREATE FUNCTION inviteme.set_table_activated_at() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW.status = 'ACTIVE' AND NEW.activated_at IS NULL THEN
        IF TG_OP = 'INSERT' THEN
            NEW.activated_at := NOW();
        ELSIF OLD.status IS DISTINCT FROM 'ACTIVE' THEN
            NEW.activated_at := NOW();
        END IF;
    END IF;

    RETURN NEW;
END;
$$;


--
-- Name: set_updated_at(); Type: FUNCTION; Schema: inviteme; Owner: -
--

CREATE FUNCTION inviteme.set_updated_at() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'inviteme', 'pg_temp'
    AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$;


--
-- Name: table_occupied_seats(uuid, uuid, uuid); Type: FUNCTION; Schema: inviteme; Owner: -
--

CREATE FUNCTION inviteme.table_occupied_seats(p_table_id uuid, p_exclude_assignment uuid DEFAULT NULL::uuid, p_exclude_walkin uuid DEFAULT NULL::uuid) RETURNS integer
    LANGUAGE sql STABLE
    SET search_path TO 'inviteme', 'pg_temp'
    AS $$
    SELECT (SELECT COUNT(*)
              FROM seating_assignments sa
             WHERE sa.table_id = p_table_id
               AND sa.id IS DISTINCT FROM p_exclude_assignment)::INTEGER
         + (SELECT COALESCE(SUM(w.party_size), 0)
              FROM walk_ins w
             WHERE w.table_id = p_table_id
               AND w.id IS DISTINCT FROM p_exclude_walkin)::INTEGER;
$$;


--
-- Name: validate_checkin_scope(); Type: FUNCTION; Schema: inviteme; Owner: -
--

CREATE FUNCTION inviteme.validate_checkin_scope() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'inviteme', 'pg_temp'
    AS $$
DECLARE
    v_target_wedding UUID;
BEGIN
    IF NEW.participant_id IS NOT NULL THEN
        SELECT g.wedding_id
          INTO v_target_wedding
          FROM guest_participants gp
          JOIN guests g ON g.id = gp.guest_id
         WHERE gp.id = NEW.participant_id;
    ELSE
        SELECT w.wedding_id
          INTO v_target_wedding
          FROM walk_ins w
         WHERE w.id = NEW.walkin_id;
    END IF;

    IF v_target_wedding IS NULL OR v_target_wedding <> NEW.wedding_id THEN
        RAISE EXCEPTION 'Check-in target does not belong to wedding';
    END IF;

    RETURN NEW;
END;
$$;


--
-- Name: validate_event_venue_scope(); Type: FUNCTION; Schema: inviteme; Owner: -
--

CREATE FUNCTION inviteme.validate_event_venue_scope() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'inviteme', 'pg_temp'
    AS $$
DECLARE
    v_venue_wedding UUID;
BEGIN
    IF NEW.venue_id IS NULL THEN
        RETURN NEW;
    END IF;

    SELECT wedding_id
      INTO v_venue_wedding
      FROM venues
     WHERE id = NEW.venue_id;

    IF v_venue_wedding IS NULL OR v_venue_wedding <> NEW.wedding_id THEN
        RAISE EXCEPTION 'Venue does not belong to wedding';
    END IF;

    RETURN NEW;
END;
$$;


--
-- Name: validate_gift_scope(); Type: FUNCTION; Schema: inviteme; Owner: -
--

CREATE FUNCTION inviteme.validate_gift_scope() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'inviteme', 'pg_temp'
    AS $$
DECLARE
    v_target_wedding UUID;
BEGIN
    IF NEW.guest_id IS NOT NULL THEN
        SELECT g.wedding_id
          INTO v_target_wedding
          FROM guests g
         WHERE g.id = NEW.guest_id;

        IF v_target_wedding IS NULL OR v_target_wedding <> NEW.wedding_id THEN
            RAISE EXCEPTION 'Gift Wedding Guest does not belong to wedding';
        END IF;
    END IF;

    IF NEW.walkin_id IS NOT NULL THEN
        SELECT w.wedding_id
          INTO v_target_wedding
          FROM walk_ins w
         WHERE w.id = NEW.walkin_id;

        IF v_target_wedding IS NULL OR v_target_wedding <> NEW.wedding_id THEN
            RAISE EXCEPTION 'Gift walk-in does not belong to wedding';
        END IF;
    END IF;

    RETURN NEW;
END;
$$;


--
-- Name: validate_guest_group_scope(); Type: FUNCTION; Schema: inviteme; Owner: -
--

CREATE FUNCTION inviteme.validate_guest_group_scope() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'inviteme', 'pg_temp'
    AS $$
DECLARE
    v_group_wedding UUID;
BEGIN
    IF NEW.group_id IS NULL THEN
        RETURN NEW;
    END IF;

    SELECT wedding_id
      INTO v_group_wedding
      FROM guest_groups
     WHERE id = NEW.group_id;

    IF v_group_wedding IS NULL OR v_group_wedding <> NEW.wedding_id THEN
        RAISE EXCEPTION 'Guest group does not belong to wedding';
    END IF;

    RETURN NEW;
END;
$$;


--
-- Name: validate_invitation_scope(); Type: FUNCTION; Schema: inviteme; Owner: -
--

CREATE FUNCTION inviteme.validate_invitation_scope() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'inviteme', 'pg_temp'
    AS $$
DECLARE
    v_guest_wedding UUID;
BEGIN
    SELECT wedding_id
      INTO v_guest_wedding
      FROM guests
     WHERE id = NEW.guest_id;

    IF v_guest_wedding IS NULL OR v_guest_wedding <> NEW.wedding_id THEN
        RAISE EXCEPTION 'Guest does not belong to wedding';
    END IF;

    RETURN NEW;
END;
$$;


--
-- Name: validate_seating_assignment(); Type: FUNCTION; Schema: inviteme; Owner: -
--

CREATE FUNCTION inviteme.validate_seating_assignment() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'inviteme', 'pg_temp'
    AS $$
DECLARE
    v_table_capacity      INTEGER;
    v_table_wedding_id    UUID;
    v_table_status        VARCHAR(20);
    v_participant_wedding UUID;
    v_attendance_status   VARCHAR(20);
    v_seat_table_id       UUID;
BEGIN
    SELECT t.capacity, t.wedding_id, t.status
      INTO v_table_capacity, v_table_wedding_id, v_table_status
      FROM tables t
     WHERE t.id = NEW.table_id
     FOR UPDATE;

    IF v_table_wedding_id IS NULL THEN
        RAISE EXCEPTION 'Table % does not exist', NEW.table_id;
    END IF;
    IF v_table_wedding_id <> NEW.wedding_id THEN
        RAISE EXCEPTION 'Table does not belong to wedding';
    END IF;
    IF v_table_status <> 'ACTIVE' THEN
        RAISE EXCEPTION 'TABLE_NOT_SEATABLE: table % is %', NEW.table_id, v_table_status;
    END IF;

    SELECT g.wedding_id, gp.attendance_status
      INTO v_participant_wedding, v_attendance_status
      FROM guest_participants gp
      JOIN guests g ON g.id = gp.guest_id
     WHERE gp.id = NEW.participant_id;

    IF v_participant_wedding IS NULL THEN
        RAISE EXCEPTION 'Participant % does not exist', NEW.participant_id;
    END IF;
    IF v_participant_wedding <> NEW.wedding_id THEN
        RAISE EXCEPTION 'Participant does not belong to wedding';
    END IF;
    IF v_attendance_status <> 'ATTENDING' THEN
        RAISE EXCEPTION 'Only ATTENDING participants can be seated';
    END IF;

    IF NEW.seat_id IS NOT NULL THEN
        SELECT s.table_id INTO v_seat_table_id FROM seats s WHERE s.id = NEW.seat_id;
        IF v_seat_table_id IS NULL OR v_seat_table_id <> NEW.table_id THEN
            RAISE EXCEPTION 'Seat does not belong to selected table';
        END IF;
    END IF;

    IF table_occupied_seats(
           NEW.table_id,
           CASE WHEN TG_OP = 'UPDATE' THEN OLD.id END,
           NULL) >= v_table_capacity THEN
        RAISE EXCEPTION 'Table capacity exceeded';
    END IF;

    RETURN NEW;
END;
$$;


--
-- Name: validate_waitlist_scope(); Type: FUNCTION; Schema: inviteme; Owner: -
--

CREATE FUNCTION inviteme.validate_waitlist_scope() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'inviteme', 'pg_temp'
    AS $$
DECLARE
    v_guest_wedding UUID;
    v_participant_guest UUID;
BEGIN
    SELECT wedding_id
      INTO v_guest_wedding
      FROM guests
     WHERE id = NEW.guest_id;

    IF v_guest_wedding IS NULL OR v_guest_wedding <> NEW.wedding_id THEN
        RAISE EXCEPTION 'Waitlist guest does not belong to wedding';
    END IF;

    IF NEW.participant_id IS NOT NULL THEN
        SELECT guest_id
          INTO v_participant_guest
          FROM guest_participants
         WHERE id = NEW.participant_id;

        IF v_participant_guest IS NULL OR v_participant_guest <> NEW.guest_id THEN
            RAISE EXCEPTION 'Waitlist participant does not belong to guest';
        END IF;
    END IF;

    RETURN NEW;
END;
$$;


--
-- Name: validate_walkin(); Type: FUNCTION; Schema: inviteme; Owner: -
--

CREATE FUNCTION inviteme.validate_walkin() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'inviteme', 'pg_temp'
    AS $$
DECLARE
    v_wedding  UUID;
    v_capacity INTEGER;
    v_status   VARCHAR(20);
BEGIN
    IF NEW.related_guest_id IS NOT NULL THEN
        SELECT wedding_id INTO v_wedding FROM guests WHERE id = NEW.related_guest_id;
        IF v_wedding IS DISTINCT FROM NEW.wedding_id THEN
            RAISE EXCEPTION 'Related guest does not belong to wedding';
        END IF;
    END IF;

    IF NEW.table_id IS NOT NULL THEN
        SELECT wedding_id, capacity, status
          INTO v_wedding, v_capacity, v_status
          FROM tables
         WHERE id = NEW.table_id
         FOR UPDATE;

        IF v_wedding IS DISTINCT FROM NEW.wedding_id THEN
            RAISE EXCEPTION 'Table does not belong to wedding';
        END IF;
        IF v_status <> 'ACTIVE' THEN
            RAISE EXCEPTION 'TABLE_NOT_SEATABLE: table % is %', NEW.table_id, v_status;
        END IF;
        IF table_occupied_seats(
               NEW.table_id,
               NULL,
               CASE WHEN TG_OP = 'UPDATE' THEN OLD.id END) + NEW.party_size > v_capacity THEN
            RAISE EXCEPTION 'Table capacity exceeded';
        END IF;
    END IF;

    RETURN NEW;
END;
$$;


SET default_tablespace = '';

SET default_table_access_method = heap;

--
-- Name: ai_generations; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.ai_generations (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    wedding_id uuid NOT NULL,
    type character varying(50) NOT NULL,
    input_ref character varying(255),
    input_json jsonb,
    output_json jsonb,
    model character varying(100),
    status character varying(20) DEFAULT 'PENDING'::character varying NOT NULL,
    created_by uuid,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_ai_generations_status CHECK (((status)::text = ANY ((ARRAY['PENDING'::character varying, 'SUCCEEDED'::character varying, 'FAILED'::character varying])::text[]))),
    CONSTRAINT ck_ai_generations_type CHECK (((type)::text = ANY ((ARRAY['GUEST_CLASSIFICATION'::character varying, 'SEATING_SUGGESTION'::character varying, 'CONTENT_GENERATION'::character varying, 'THANK_YOU'::character varying])::text[])))
);


--
-- Name: audit_logs; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.audit_logs (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    wedding_id uuid,
    actor_type character varying(20) NOT NULL,
    actor_user_id uuid,
    actor_guest_id uuid,
    action character varying(100) NOT NULL,
    entity_type character varying(100) NOT NULL,
    entity_id uuid,
    metadata jsonb,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_audit_logs_actor_shape CHECK (((((actor_type)::text = 'USER'::text) AND (actor_user_id IS NOT NULL) AND (actor_guest_id IS NULL)) OR (((actor_type)::text = 'WEDDING_GUEST'::text) AND (actor_guest_id IS NOT NULL) AND (actor_user_id IS NULL)) OR (((actor_type)::text = ANY ((ARRAY['SYSTEM'::character varying, 'AI'::character varying])::text[])) AND (actor_user_id IS NULL) AND (actor_guest_id IS NULL)))),
    CONSTRAINT ck_audit_logs_actor_type CHECK (((actor_type)::text = ANY ((ARRAY['USER'::character varying, 'WEDDING_GUEST'::character varying, 'SYSTEM'::character varying, 'AI'::character varying])::text[])))
);


--
-- Name: check_ins; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.check_ins (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    wedding_id uuid NOT NULL,
    participant_id uuid,
    walkin_id uuid,
    checked_in_by uuid NOT NULL,
    status character varying(20) DEFAULT 'CHECKED_IN'::character varying NOT NULL,
    checked_in_at timestamp with time zone DEFAULT now() NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    method character varying(20) DEFAULT 'QR'::character varying NOT NULL,
    onsite_override_reason character varying(30),
    voided_by uuid,
    voided_at timestamp with time zone,
    void_reason text,
    CONSTRAINT ck_checkins_method CHECK (((method)::text = ANY ((ARRAY['QR'::character varying, 'MANUAL'::character varying, 'WALK_IN'::character varying])::text[]))),
    CONSTRAINT ck_checkins_method_target CHECK ((((method)::text = 'WALK_IN'::text) = (walkin_id IS NOT NULL))),
    CONSTRAINT ck_checkins_override_reason CHECK (((onsite_override_reason IS NULL) OR ((onsite_override_reason)::text = ANY ((ARRAY['DECLINED_ARRIVED'::character varying, 'PENDING_ARRIVED'::character varying, 'WAITLISTED_ARRIVED'::character varying])::text[])))),
    CONSTRAINT ck_checkins_status CHECK (((status)::text = ANY ((ARRAY['CHECKED_IN'::character varying, 'VOID'::character varying])::text[]))),
    CONSTRAINT ck_checkins_target_xor CHECK ((((participant_id IS NOT NULL) AND (walkin_id IS NULL)) OR ((participant_id IS NULL) AND (walkin_id IS NOT NULL)))),
    CONSTRAINT ck_checkins_void_shape CHECK (((((status)::text = 'CHECKED_IN'::text) AND (voided_at IS NULL) AND (voided_by IS NULL) AND (void_reason IS NULL)) OR (((status)::text = 'VOID'::text) AND (voided_at IS NOT NULL) AND (void_reason IS NOT NULL))))
);


--
-- Name: gift_messages; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.gift_messages (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    gift_id uuid NOT NULL,
    message text NOT NULL,
    visibility character varying(20) DEFAULT 'PRIVATE'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_gift_messages_visibility CHECK (((visibility)::text = ANY ((ARRAY['PRIVATE'::character varying, 'PUBLIC'::character varying, 'HIDDEN'::character varying])::text[])))
);


--
-- Name: gifts; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.gifts (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    wedding_id uuid NOT NULL,
    guest_id uuid,
    walkin_id uuid,
    amount numeric(14,2) DEFAULT 0 NOT NULL,
    currency character(3) DEFAULT 'VND'::bpchar NOT NULL,
    method character varying(30) NOT NULL,
    provider character varying(50),
    transaction_ref character varying(150),
    idempotency_key character varying(150),
    transaction_status character varying(30) DEFAULT 'COMPLETED'::character varying NOT NULL,
    received_by uuid,
    received_at timestamp with time zone DEFAULT now() NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_gifts_amount CHECK ((amount >= (0)::numeric)),
    CONSTRAINT ck_gifts_currency CHECK ((currency ~ '^[A-Z]{3}$'::text)),
    CONSTRAINT ck_gifts_method CHECK (((method)::text = ANY ((ARRAY['CASH'::character varying, 'BANK_QR'::character varying, 'BANK_TRANSFER'::character varying, 'OTHER'::character varying])::text[]))),
    CONSTRAINT ck_gifts_source_xor CHECK ((((guest_id IS NOT NULL) AND (walkin_id IS NULL)) OR ((guest_id IS NULL) AND (walkin_id IS NOT NULL)))),
    CONSTRAINT ck_gifts_transaction_status CHECK (((transaction_status)::text = ANY ((ARRAY['PENDING'::character varying, 'COMPLETED'::character varying, 'FAILED'::character varying, 'REFUNDED'::character varying])::text[])))
);


--
-- Name: TABLE gifts; Type: COMMENT; Schema: inviteme; Owner: -
--

COMMENT ON TABLE inviteme.gifts IS 'Gift record independent from RSVP attendance; a Wedding Guest may send a gift even when RSVP is DECLINED.';


--
-- Name: guest_groups; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.guest_groups (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    wedding_id uuid NOT NULL,
    name character varying(150) NOT NULL,
    side character varying(20),
    description text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_guest_groups_side CHECK (((side IS NULL) OR ((side)::text = ANY ((ARRAY['BRIDE'::character varying, 'GROOM'::character varying, 'MUTUAL'::character varying, 'OTHER'::character varying])::text[]))))
);


--
-- Name: guest_notes; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.guest_notes (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    guest_id uuid NOT NULL,
    participant_id uuid,
    created_by uuid,
    note_type character varying(30) DEFAULT 'GENERAL'::character varying NOT NULL,
    content text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: guest_participants; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.guest_participants (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    guest_id uuid NOT NULL,
    full_name character varying(150) NOT NULL,
    participant_type character varying(20) NOT NULL,
    attendance_status character varying(20) DEFAULT 'PENDING'::character varying NOT NULL,
    dietary_note text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_guest_participant_attendance CHECK (((attendance_status)::text = ANY ((ARRAY['PENDING'::character varying, 'ATTENDING'::character varying, 'DECLINED'::character varying, 'WAITLISTED'::character varying])::text[]))),
    CONSTRAINT ck_guest_participant_type CHECK (((participant_type)::text = ANY ((ARRAY['PRIMARY'::character varying, 'SPOUSE'::character varying, 'CHILD'::character varying, 'PLUS_ONE'::character varying, 'OTHER'::character varying])::text[])))
);


--
-- Name: guests; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.guests (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    wedding_id uuid NOT NULL,
    group_id uuid,
    guest_code character varying(50) NOT NULL,
    full_name character varying(150) NOT NULL,
    phone character varying(30),
    email character varying(255),
    relationship character varying(100),
    side character varying(20) DEFAULT 'MUTUAL'::character varying NOT NULL,
    record_status character varying(20) DEFAULT 'ACTIVE'::character varying NOT NULL,
    allowed_plus_one boolean DEFAULT false NOT NULL,
    max_plus_one integer DEFAULT 0 NOT NULL,
    expected_companion_count integer DEFAULT 0 NOT NULL,
    notes text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_guests_expected_companion_count CHECK ((expected_companion_count >= 0)),
    CONSTRAINT ck_guests_plus_one CHECK (((max_plus_one >= 0) AND (allowed_plus_one OR (max_plus_one = 0)))),
    CONSTRAINT ck_guests_record_status CHECK (((record_status)::text = ANY ((ARRAY['ACTIVE'::character varying, 'ARCHIVED'::character varying])::text[]))),
    CONSTRAINT ck_guests_side CHECK (((side)::text = ANY ((ARRAY['BRIDE'::character varying, 'GROOM'::character varying, 'MUTUAL'::character varying, 'OTHER'::character varying])::text[])))
);


--
-- Name: TABLE guests; Type: COMMENT; Schema: inviteme; Owner: -
--

COMMENT ON TABLE inviteme.guests IS 'Physical data entity for the Wedding Guest actor registered in a wedding guest list.';


--
-- Name: COLUMN guests.expected_companion_count; Type: COMMENT; Schema: inviteme; Owner: -
--

COMMENT ON COLUMN inviteme.guests.expected_companion_count IS 'Estimated number of accompanying people before RSVP; estimated party size = 1 + expected_companion_count.';


--
-- Name: invitation_deliveries; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.invitation_deliveries (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    invitation_id uuid NOT NULL,
    channel character varying(20) NOT NULL,
    recipient character varying(255) NOT NULL,
    status character varying(20) DEFAULT 'PENDING'::character varying NOT NULL,
    provider_message_id character varying(255),
    sent_at timestamp with time zone,
    delivered_at timestamp with time zone,
    failed_at timestamp with time zone,
    error_message text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    request_key character varying(32),
    is_sandbox boolean DEFAULT false NOT NULL,
    publication_hash character(64),
    lease_id uuid,
    lease_until timestamp with time zone,
    attempt_count integer DEFAULT 0 NOT NULL,
    CONSTRAINT ck_invitation_delivery_channel CHECK (((channel)::text = ANY ((ARRAY['EMAIL'::character varying, 'SMS'::character varying])::text[]))),
    CONSTRAINT ck_invitation_delivery_status CHECK (((status)::text = ANY ((ARRAY['PENDING'::character varying, 'SENT'::character varying, 'DELIVERED'::character varying, 'FAILED'::character varying])::text[])))
);


--
-- Name: invitations; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.invitations (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    wedding_id uuid NOT NULL,
    guest_id uuid NOT NULL,
    token_hash character(64) NOT NULL,
    status character varying(20) DEFAULT 'DRAFT'::character varying NOT NULL,
    configuration jsonb,
    previewed_at timestamp with time zone,
    approved_by uuid,
    approved_at timestamp with time zone,
    published_at timestamp with time zone,
    expires_at timestamp with time zone,
    sent_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    version integer DEFAULT 1 NOT NULL,
    published_configuration jsonb,
    protected_token text,
    reviewed_at timestamp with time zone,
    opened_at timestamp with time zone,
    CONSTRAINT ck_invitations_status CHECK (((status)::text = ANY ((ARRAY['DRAFT'::character varying, 'IN_REVIEW'::character varying, 'APPROVED'::character varying, 'PUBLISHED'::character varying, 'SENT'::character varying, 'OPENED'::character varying, 'REVOKED'::character varying, 'EXPIRED'::character varying])::text[])))
);


--
-- Name: love_stories; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.love_stories (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    wedding_id uuid NOT NULL,
    title character varying(200),
    content text NOT NULL,
    visibility character varying(20) DEFAULT 'VISIBLE'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_love_stories_visibility CHECK (((visibility)::text = ANY ((ARRAY['VISIBLE'::character varying, 'HIDDEN'::character varying, 'PRIVATE'::character varying])::text[])))
);


--
-- Name: notifications; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.notifications (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    wedding_id uuid NOT NULL,
    recipient_user_id uuid,
    recipient_guest_id uuid,
    notification_type character varying(30) NOT NULL,
    channel character varying(20) NOT NULL,
    template character varying(100),
    content text NOT NULL,
    status character varying(20) DEFAULT 'PENDING'::character varying NOT NULL,
    scheduled_at timestamp with time zone,
    sent_at timestamp with time zone,
    attempt_count integer DEFAULT 0 NOT NULL,
    last_error text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_notifications_attempt_count CHECK ((attempt_count >= 0)),
    CONSTRAINT ck_notifications_channel CHECK (((channel)::text = ANY ((ARRAY['IN_APP'::character varying, 'EMAIL'::character varying, 'SMS'::character varying])::text[]))),
    CONSTRAINT ck_notifications_recipient_xor CHECK ((((recipient_user_id IS NOT NULL) AND (recipient_guest_id IS NULL)) OR ((recipient_user_id IS NULL) AND (recipient_guest_id IS NOT NULL)))),
    CONSTRAINT ck_notifications_status CHECK (((status)::text = ANY ((ARRAY['PENDING'::character varying, 'SCHEDULED'::character varying, 'SENT'::character varying, 'FAILED'::character varying, 'CANCELLED'::character varying])::text[])))
);


--
-- Name: permissions; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.permissions (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    code character varying(100) NOT NULL,
    name character varying(150) NOT NULL,
    description text,
    scope character varying(30) DEFAULT 'WEDDING'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_permissions_scope CHECK (((scope)::text = ANY ((ARRAY['PLATFORM'::character varying, 'WEDDING'::character varying])::text[])))
);


--
-- Name: plans; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.plans (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    code character varying(50) NOT NULL,
    name character varying(100) NOT NULL,
    price numeric(14,2) DEFAULT 0 NOT NULL,
    currency character(3) DEFAULT 'VND'::bpchar NOT NULL,
    billing_period character varying(20) DEFAULT 'ONE_TIME'::character varying NOT NULL,
    limits_json jsonb,
    status character varying(20) DEFAULT 'ACTIVE'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_plans_billing_period CHECK (((billing_period)::text = ANY ((ARRAY['ONE_TIME'::character varying, 'MONTHLY'::character varying, 'YEARLY'::character varying])::text[]))),
    CONSTRAINT ck_plans_currency CHECK ((currency ~ '^[A-Z]{3}$'::text)),
    CONSTRAINT ck_plans_price CHECK ((price >= (0)::numeric)),
    CONSTRAINT ck_plans_status CHECK (((status)::text = ANY ((ARRAY['ACTIVE'::character varying, 'INACTIVE'::character varying])::text[])))
);


--
-- Name: role_claims; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.role_claims (
    id integer NOT NULL,
    role_id uuid NOT NULL,
    claim_type text,
    claim_value text
);


--
-- Name: role_claims_id_seq; Type: SEQUENCE; Schema: inviteme; Owner: -
--

ALTER TABLE inviteme.role_claims ALTER COLUMN id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME inviteme.role_claims_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: role_permissions; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.role_permissions (
    role_id uuid NOT NULL,
    permission_id uuid NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: roles; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.roles (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    code character varying(50) NOT NULL,
    name character varying(100) NOT NULL,
    description text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    normalized_name character varying(256),
    concurrency_stamp text
);


--
-- Name: rsvp_history; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.rsvp_history (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    rsvp_id uuid NOT NULL,
    old_status character varying(20),
    new_status character varying(20) NOT NULL,
    old_confirmed_party_size integer,
    new_confirmed_party_size integer NOT NULL,
    old_snapshot jsonb,
    new_snapshot jsonb,
    changed_by uuid,
    reason text,
    changed_at timestamp with time zone DEFAULT now() NOT NULL,
    actor_type character varying(20) NOT NULL,
    actor_guest_id uuid,
    CONSTRAINT ck_rsvp_history_actor_shape CHECK (((((actor_type)::text = 'USER'::text) AND (changed_by IS NOT NULL) AND (actor_guest_id IS NULL)) OR (((actor_type)::text = 'WEDDING_GUEST'::text) AND (actor_guest_id IS NOT NULL) AND (changed_by IS NULL)) OR (((actor_type)::text = 'SYSTEM'::text) AND (changed_by IS NULL) AND (actor_guest_id IS NULL)))),
    CONSTRAINT ck_rsvp_history_actor_type CHECK (((actor_type)::text = ANY ((ARRAY['USER'::character varying, 'WEDDING_GUEST'::character varying, 'SYSTEM'::character varying])::text[]))),
    CONSTRAINT ck_rsvp_history_confirmed_party_sizes CHECK ((((old_confirmed_party_size IS NULL) OR (old_confirmed_party_size >= 0)) AND (new_confirmed_party_size >= 0))),
    CONSTRAINT ck_rsvp_history_new_status CHECK (((new_status)::text = ANY ((ARRAY['PENDING'::character varying, 'ATTENDING'::character varying, 'DECLINED'::character varying])::text[]))),
    CONSTRAINT ck_rsvp_history_old_status CHECK (((old_status IS NULL) OR ((old_status)::text = ANY ((ARRAY['PENDING'::character varying, 'ATTENDING'::character varying, 'DECLINED'::character varying])::text[]))))
);


--
-- Name: TABLE rsvp_history; Type: COMMENT; Schema: inviteme; Owner: -
--

COMMENT ON TABLE inviteme.rsvp_history IS 'Append-only RSVP change log (RSVP-02). One row per real change, written in the same transaction as the rsvps update.';


--
-- Name: rsvps; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.rsvps (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    invitation_id uuid NOT NULL,
    status character varying(20) DEFAULT 'PENDING'::character varying NOT NULL,
    confirmed_party_size integer DEFAULT 0 NOT NULL,
    submitted_at timestamp with time zone,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_rsvps_confirmed_party_size CHECK ((confirmed_party_size >= 0)),
    CONSTRAINT ck_rsvps_status CHECK (((status)::text = ANY ((ARRAY['PENDING'::character varying, 'ATTENDING'::character varying, 'DECLINED'::character varying])::text[]))),
    CONSTRAINT ck_rsvps_status_party_size CHECK (((((status)::text = ANY ((ARRAY['PENDING'::character varying, 'DECLINED'::character varying])::text[])) AND (confirmed_party_size = 0)) OR (((status)::text = 'ATTENDING'::text) AND (confirmed_party_size >= 1))))
);


--
-- Name: COLUMN rsvps.confirmed_party_size; Type: COMMENT; Schema: inviteme; Owner: -
--

COMMENT ON COLUMN inviteme.rsvps.confirmed_party_size IS 'Final party size confirmed through RSVP; distinct from the pre-RSVP estimated party size.';


--
-- Name: seating_assignments; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.seating_assignments (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    wedding_id uuid NOT NULL,
    participant_id uuid NOT NULL,
    table_id uuid NOT NULL,
    seat_id uuid,
    assigned_by uuid NOT NULL,
    version integer DEFAULT 1 NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_seating_assignment_version CHECK ((version >= 1))
);


--
-- Name: seating_change_logs; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.seating_change_logs (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    wedding_id uuid NOT NULL,
    assignment_id uuid,
    participant_id uuid NOT NULL,
    actor_id uuid NOT NULL,
    from_table_id uuid,
    to_table_id uuid,
    from_seat_id uuid,
    to_seat_id uuid,
    action character varying(30) NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_seating_logs_action CHECK (((action)::text = ANY ((ARRAY['ASSIGN'::character varying, 'MOVE'::character varying, 'UNASSIGN'::character varying, 'SEAT_CHANGE'::character varying])::text[])))
);


--
-- Name: seats; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.seats (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    table_id uuid NOT NULL,
    seat_number integer NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_seats_number CHECK ((seat_number > 0))
);


--
-- Name: subscriptions; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.subscriptions (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    user_id uuid NOT NULL,
    plan_id uuid NOT NULL,
    status character varying(20) DEFAULT 'ACTIVE'::character varying NOT NULL,
    started_at timestamp with time zone DEFAULT now() NOT NULL,
    expires_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_subscriptions_dates CHECK (((expires_at IS NULL) OR (expires_at >= started_at))),
    CONSTRAINT ck_subscriptions_status CHECK (((status)::text = ANY ((ARRAY['ACTIVE'::character varying, 'EXPIRED'::character varying, 'CANCELLED'::character varying, 'SUSPENDED'::character varying])::text[])))
);


--
-- Name: table_status_history; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.table_status_history (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    wedding_id uuid NOT NULL,
    table_id uuid NOT NULL,
    from_status character varying(20),
    to_status character varying(20) NOT NULL,
    reason_code character varying(30),
    note text,
    overflow_snapshot jsonb,
    changed_by uuid,
    changed_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_table_status_history_backup_activation_reason CHECK (((NOT (((from_status)::text = 'BACKUP'::text) AND ((to_status)::text = 'ACTIVE'::text))) OR (reason_code IS NOT NULL))),
    CONSTRAINT ck_table_status_history_from_status CHECK (((from_status IS NULL) OR ((from_status)::text = ANY ((ARRAY['PLANNED'::character varying, 'ACTIVE'::character varying, 'BACKUP'::character varying, 'INACTIVE'::character varying])::text[])))),
    CONSTRAINT ck_table_status_history_other_note CHECK ((((reason_code)::text IS DISTINCT FROM 'OTHER'::text) OR (note IS NOT NULL))),
    CONSTRAINT ck_table_status_history_reason_code CHECK (((reason_code IS NULL) OR ((reason_code)::text = ANY ((ARRAY['EXTRA_COMPANIONS'::character varying, 'WAITLIST_PROMOTION'::character varying, 'LATE_RSVP_CHANGE'::character varying, 'WALK_IN'::character varying, 'PLANNING_SHORTFALL'::character varying, 'OTHER'::character varying])::text[])))),
    CONSTRAINT ck_table_status_history_to_status CHECK (((to_status)::text = ANY ((ARRAY['PLANNED'::character varying, 'ACTIVE'::character varying, 'BACKUP'::character varying, 'INACTIVE'::character varying])::text[])))
);


--
-- Name: tables; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.tables (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    wedding_id uuid NOT NULL,
    table_number character varying(100) NOT NULL,
    capacity integer NOT NULL,
    status character varying(20) DEFAULT 'PLANNED'::character varying NOT NULL,
    version integer DEFAULT 1 NOT NULL,
    activated_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    table_kind character varying(20) DEFAULT 'PRIMARY'::character varying NOT NULL,
    CONSTRAINT ck_tables_capacity CHECK ((capacity > 0)),
    CONSTRAINT ck_tables_kind CHECK (((table_kind)::text = ANY ((ARRAY['PRIMARY'::character varying, 'BACKUP'::character varying])::text[]))),
    CONSTRAINT ck_tables_kind_status CHECK (((((table_kind)::text = 'PRIMARY'::text) AND ((status)::text = ANY ((ARRAY['PLANNED'::character varying, 'ACTIVE'::character varying, 'INACTIVE'::character varying])::text[]))) OR (((table_kind)::text = 'BACKUP'::text) AND ((status)::text = ANY ((ARRAY['BACKUP'::character varying, 'ACTIVE'::character varying, 'INACTIVE'::character varying])::text[]))))),
    CONSTRAINT ck_tables_status CHECK (((status)::text = ANY ((ARRAY['PLANNED'::character varying, 'ACTIVE'::character varying, 'BACKUP'::character varying, 'INACTIVE'::character varying])::text[]))),
    CONSTRAINT ck_tables_version CHECK ((version >= 1))
);


--
-- Name: COLUMN tables.status; Type: COMMENT; Schema: inviteme; Owner: -
--

COMMENT ON COLUMN inviteme.tables.status IS 'Operational table lifecycle: PLANNED, ACTIVE, BACKUP, INACTIVE.';


--
-- Name: template_sections; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.template_sections (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    template_id uuid NOT NULL,
    section_key character varying(100) NOT NULL,
    config_json jsonb,
    sort_order integer DEFAULT 0 NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: templates; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.templates (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    name character varying(150) NOT NULL,
    slug character varying(160) NOT NULL,
    theme character varying(100),
    preview_url text,
    configuration jsonb,
    status character varying(20) DEFAULT 'ACTIVE'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_templates_status CHECK (((status)::text = ANY ((ARRAY['ACTIVE'::character varying, 'INACTIVE'::character varying, 'DRAFT'::character varying])::text[])))
);


--
-- Name: user_claims; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.user_claims (
    id integer NOT NULL,
    user_id uuid NOT NULL,
    claim_type text,
    claim_value text
);


--
-- Name: user_claims_id_seq; Type: SEQUENCE; Schema: inviteme; Owner: -
--

ALTER TABLE inviteme.user_claims ALTER COLUMN id ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME inviteme.user_claims_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: user_logins; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.user_logins (
    login_provider character varying(128) NOT NULL,
    provider_key character varying(128) NOT NULL,
    provider_display_name text,
    user_id uuid NOT NULL
);


--
-- Name: user_roles; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.user_roles (
    user_id uuid NOT NULL,
    role_id uuid NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: user_tokens; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.user_tokens (
    user_id uuid NOT NULL,
    login_provider character varying(128) NOT NULL,
    name character varying(128) NOT NULL,
    value text
);


--
-- Name: users; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.users (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    email character varying(255) NOT NULL,
    password_hash character varying(255),
    full_name character varying(150) NOT NULL,
    phone character varying(30),
    avatar_url text,
    status character varying(20) DEFAULT 'ACTIVE'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    user_name character varying(256),
    normalized_user_name character varying(256),
    normalized_email character varying(256),
    email_confirmed boolean DEFAULT false NOT NULL,
    security_stamp text,
    concurrency_stamp text,
    phone_number_confirmed boolean DEFAULT false NOT NULL,
    two_factor_enabled boolean DEFAULT false NOT NULL,
    lockout_end timestamp with time zone,
    lockout_enabled boolean DEFAULT true NOT NULL,
    access_failed_count integer DEFAULT 0 NOT NULL,
    CONSTRAINT ck_users_status CHECK (((status)::text = ANY ((ARRAY['ACTIVE'::character varying, 'SUSPENDED'::character varying, 'DISABLED'::character varying])::text[])))
);


--
-- Name: v_guest_rsvp_summary; Type: VIEW; Schema: inviteme; Owner: -
--

CREATE VIEW inviteme.v_guest_rsvp_summary AS
 SELECT g.id AS guest_id,
    g.wedding_id,
    g.guest_code,
    g.full_name,
    (1 + g.expected_companion_count) AS estimated_party_size,
    i.id AS invitation_id,
    i.status AS invitation_status,
    r.id AS rsvp_id,
    r.status AS rsvp_status,
    r.confirmed_party_size,
    count(gp.id) FILTER (WHERE ((gp.attendance_status)::text = 'ATTENDING'::text)) AS attending_participants,
    count(gp.id) FILTER (WHERE ((gp.attendance_status)::text = 'WAITLISTED'::text)) AS waitlisted_participants
   FROM (((inviteme.guests g
     LEFT JOIN inviteme.invitations i ON (((i.guest_id = g.id) AND (i.wedding_id = g.wedding_id))))
     LEFT JOIN inviteme.rsvps r ON ((r.invitation_id = i.id)))
     LEFT JOIN inviteme.guest_participants gp ON ((gp.guest_id = g.id)))
  GROUP BY g.id, g.wedding_id, g.guest_code, g.full_name, g.expected_companion_count, i.id, i.status, r.id, r.status, r.confirmed_party_size;


--
-- Name: v_table_occupancy; Type: VIEW; Schema: inviteme; Owner: -
--

CREATE VIEW inviteme.v_table_occupancy AS
 SELECT id AS table_id,
    wedding_id,
    table_number,
    capacity,
    status,
    (inviteme.table_occupied_seats(id))::bigint AS occupied,
    (GREATEST((capacity - inviteme.table_occupied_seats(id)), 0))::bigint AS remaining
   FROM inviteme.tables t;


--
-- Name: v_wedding_headcount; Type: VIEW; Schema: inviteme; Owner: -
--

CREATE VIEW inviteme.v_wedding_headcount AS
 SELECT g.wedding_id,
    count(*) FILTER (WHERE ((gp.attendance_status)::text = 'ATTENDING'::text)) AS confirmed_headcount,
    count(*) FILTER (WHERE ((gp.attendance_status)::text = 'WAITLISTED'::text)) AS waitlisted_participants,
    count(*) FILTER (WHERE ((gp.attendance_status)::text = 'PENDING'::text)) AS pending_participants,
    count(*) FILTER (WHERE ((gp.attendance_status)::text = 'DECLINED'::text)) AS declined_participants
   FROM (inviteme.guests g
     JOIN inviteme.guest_participants gp ON ((gp.guest_id = g.id)))
  GROUP BY g.wedding_id;


--
-- Name: venues; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.venues (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    wedding_id uuid NOT NULL,
    name character varying(200) NOT NULL,
    address text,
    latitude numeric(9,6),
    longitude numeric(9,6),
    capacity integer,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_venues_capacity CHECK (((capacity IS NULL) OR (capacity > 0))),
    CONSTRAINT ck_venues_latitude CHECK (((latitude IS NULL) OR ((latitude >= ('-90'::integer)::numeric) AND (latitude <= (90)::numeric)))),
    CONSTRAINT ck_venues_longitude CHECK (((longitude IS NULL) OR ((longitude >= ('-180'::integer)::numeric) AND (longitude <= (180)::numeric))))
);


--
-- Name: waitlist_entries; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.waitlist_entries (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    wedding_id uuid NOT NULL,
    guest_id uuid NOT NULL,
    participant_id uuid,
    requested_slots integer DEFAULT 1 NOT NULL,
    status character varying(20) DEFAULT 'WAITING'::character varying NOT NULL,
    priority integer,
    reason text,
    requested_at timestamp with time zone DEFAULT now() NOT NULL,
    promoted_at timestamp with time zone,
    cancelled_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_waitlist_requested_slots CHECK ((requested_slots > 0)),
    CONSTRAINT ck_waitlist_status CHECK (((status)::text = ANY ((ARRAY['WAITING'::character varying, 'PROMOTED'::character varying, 'CANCELLED'::character varying, 'EXPIRED'::character varying])::text[])))
);


--
-- Name: walk_ins; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.walk_ins (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    wedding_id uuid NOT NULL,
    full_name character varying(150) NOT NULL,
    phone character varying(30),
    party_size integer DEFAULT 1 NOT NULL,
    note text,
    created_by uuid NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    side character varying(20),
    related_guest_id uuid,
    table_id uuid,
    CONSTRAINT ck_walkins_full_name_not_blank CHECK ((btrim((full_name)::text) <> ''::text)),
    CONSTRAINT ck_walkins_party_size CHECK ((party_size > 0)),
    CONSTRAINT ck_walkins_side CHECK (((side IS NULL) OR ((side)::text = ANY ((ARRAY['BRIDE'::character varying, 'GROOM'::character varying, 'MUTUAL'::character varying, 'OTHER'::character varying])::text[]))))
);


--
-- Name: wedding_events; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.wedding_events (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    wedding_id uuid NOT NULL,
    venue_id uuid,
    name character varying(200) NOT NULL,
    description text,
    start_at timestamp with time zone NOT NULL,
    end_at timestamp with time zone,
    capacity_limit integer,
    sort_order integer DEFAULT 0 NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    is_main boolean DEFAULT false NOT NULL,
    CONSTRAINT ck_wedding_events_capacity CHECK (((capacity_limit IS NULL) OR (capacity_limit > 0))),
    CONSTRAINT ck_wedding_events_dates CHECK (((end_at IS NULL) OR (end_at >= start_at)))
);


--
-- Name: wedding_media; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.wedding_media (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    wedding_id uuid NOT NULL,
    media_url text NOT NULL,
    storage_key text,
    media_type character varying(30) NOT NULL,
    caption text,
    visibility character varying(20) DEFAULT 'VISIBLE'::character varying NOT NULL,
    sort_order integer DEFAULT 0 NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_wedding_media_type CHECK (((media_type)::text = ANY ((ARRAY['IMAGE'::character varying, 'VIDEO'::character varying, 'OTHER'::character varying])::text[]))),
    CONSTRAINT ck_wedding_media_visibility CHECK (((visibility)::text = ANY ((ARRAY['VISIBLE'::character varying, 'HIDDEN'::character varying, 'PRIVATE'::character varying])::text[])))
);


--
-- Name: wedding_member_permissions; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.wedding_member_permissions (
    member_id uuid NOT NULL,
    permission_id uuid NOT NULL,
    granted_at timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: wedding_members; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.wedding_members (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    wedding_id uuid NOT NULL,
    user_id uuid NOT NULL,
    member_role character varying(30) DEFAULT 'CO_HOST'::character varying NOT NULL,
    status character varying(20) DEFAULT 'ACTIVE'::character varying NOT NULL,
    joined_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_wedding_members_role CHECK (((member_role)::text = 'CO_HOST'::text)),
    CONSTRAINT ck_wedding_members_status CHECK (((status)::text = ANY ((ARRAY['INVITED'::character varying, 'ACTIVE'::character varying, 'REVOKED'::character varying])::text[])))
);


--
-- Name: wedding_settings; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.wedding_settings (
    wedding_id uuid NOT NULL,
    visibility character varying(20) DEFAULT 'PRIVATE'::character varying NOT NULL,
    timezone character varying(100) DEFAULT 'Asia/Ho_Chi_Minh'::character varying NOT NULL,
    rsvp_deadline timestamp with time zone,
    rsvp_reminder_days_before smallint DEFAULT 2 NOT NULL,
    settings_json jsonb,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    booked_table_count integer,
    backup_table_limit integer DEFAULT 0 NOT NULL,
    default_table_capacity smallint DEFAULT 10 NOT NULL,
    block_arrivals_over_capacity boolean DEFAULT false NOT NULL,
    CONSTRAINT ck_wedding_settings_backup_table_limit CHECK ((backup_table_limit >= 0)),
    CONSTRAINT ck_wedding_settings_booked_table_count CHECK (((booked_table_count IS NULL) OR (booked_table_count > 0))),
    CONSTRAINT ck_wedding_settings_default_table_capacity CHECK (((default_table_capacity >= 1) AND (default_table_capacity <= 20))),
    CONSTRAINT ck_wedding_settings_reminder_days CHECK ((rsvp_reminder_days_before >= 0)),
    CONSTRAINT ck_wedding_settings_visibility CHECK (((visibility)::text = ANY ((ARRAY['PRIVATE'::character varying, 'UNLISTED'::character varying, 'PUBLIC'::character varying])::text[])))
);


--
-- Name: weddings; Type: TABLE; Schema: inviteme; Owner: -
--

CREATE TABLE inviteme.weddings (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    owner_user_id uuid NOT NULL,
    title character varying(200) NOT NULL,
    slug character varying(180) NOT NULL,
    status character varying(20) DEFAULT 'DRAFT'::character varying NOT NULL,
    max_capacity integer,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    version integer DEFAULT 1 NOT NULL,
    CONSTRAINT ck_weddings_max_capacity CHECK (((max_capacity IS NULL) OR (max_capacity > 0))),
    CONSTRAINT ck_weddings_status CHECK (((status)::text = ANY ((ARRAY['DRAFT'::character varying, 'PUBLISHED'::character varying, 'CANCELLED'::character varying, 'ARCHIVED'::character varying])::text[])))
);


--
-- Name: ai_generations ai_generations_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.ai_generations
    ADD CONSTRAINT ai_generations_pkey PRIMARY KEY (id);


--
-- Name: audit_logs audit_logs_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.audit_logs
    ADD CONSTRAINT audit_logs_pkey PRIMARY KEY (id);


--
-- Name: check_ins check_ins_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.check_ins
    ADD CONSTRAINT check_ins_pkey PRIMARY KEY (id);


--
-- Name: gift_messages gift_messages_gift_id_key; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.gift_messages
    ADD CONSTRAINT gift_messages_gift_id_key UNIQUE (gift_id);


--
-- Name: gift_messages gift_messages_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.gift_messages
    ADD CONSTRAINT gift_messages_pkey PRIMARY KEY (id);


--
-- Name: gifts gifts_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.gifts
    ADD CONSTRAINT gifts_pkey PRIMARY KEY (id);


--
-- Name: guest_groups guest_groups_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.guest_groups
    ADD CONSTRAINT guest_groups_pkey PRIMARY KEY (id);


--
-- Name: guest_notes guest_notes_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.guest_notes
    ADD CONSTRAINT guest_notes_pkey PRIMARY KEY (id);


--
-- Name: guest_participants guest_participants_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.guest_participants
    ADD CONSTRAINT guest_participants_pkey PRIMARY KEY (id);


--
-- Name: guests guests_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.guests
    ADD CONSTRAINT guests_pkey PRIMARY KEY (id);


--
-- Name: invitation_deliveries invitation_deliveries_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.invitation_deliveries
    ADD CONSTRAINT invitation_deliveries_pkey PRIMARY KEY (id);


--
-- Name: invitations invitations_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.invitations
    ADD CONSTRAINT invitations_pkey PRIMARY KEY (id);


--
-- Name: invitations invitations_token_hash_key; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.invitations
    ADD CONSTRAINT invitations_token_hash_key UNIQUE (token_hash);


--
-- Name: love_stories love_stories_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.love_stories
    ADD CONSTRAINT love_stories_pkey PRIMARY KEY (id);


--
-- Name: notifications notifications_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.notifications
    ADD CONSTRAINT notifications_pkey PRIMARY KEY (id);


--
-- Name: permissions permissions_code_key; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.permissions
    ADD CONSTRAINT permissions_code_key UNIQUE (code);


--
-- Name: permissions permissions_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.permissions
    ADD CONSTRAINT permissions_pkey PRIMARY KEY (id);


--
-- Name: plans plans_code_key; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.plans
    ADD CONSTRAINT plans_code_key UNIQUE (code);


--
-- Name: plans plans_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.plans
    ADD CONSTRAINT plans_pkey PRIMARY KEY (id);


--
-- Name: role_claims role_claims_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.role_claims
    ADD CONSTRAINT role_claims_pkey PRIMARY KEY (id);


--
-- Name: role_permissions role_permissions_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.role_permissions
    ADD CONSTRAINT role_permissions_pkey PRIMARY KEY (role_id, permission_id);


--
-- Name: roles roles_code_key; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.roles
    ADD CONSTRAINT roles_code_key UNIQUE (code);


--
-- Name: roles roles_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.roles
    ADD CONSTRAINT roles_pkey PRIMARY KEY (id);


--
-- Name: rsvp_history rsvp_history_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.rsvp_history
    ADD CONSTRAINT rsvp_history_pkey PRIMARY KEY (id);


--
-- Name: rsvps rsvps_invitation_id_key; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.rsvps
    ADD CONSTRAINT rsvps_invitation_id_key UNIQUE (invitation_id);


--
-- Name: rsvps rsvps_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.rsvps
    ADD CONSTRAINT rsvps_pkey PRIMARY KEY (id);


--
-- Name: seating_assignments seating_assignments_participant_id_key; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.seating_assignments
    ADD CONSTRAINT seating_assignments_participant_id_key UNIQUE (participant_id);


--
-- Name: seating_assignments seating_assignments_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.seating_assignments
    ADD CONSTRAINT seating_assignments_pkey PRIMARY KEY (id);


--
-- Name: seating_change_logs seating_change_logs_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.seating_change_logs
    ADD CONSTRAINT seating_change_logs_pkey PRIMARY KEY (id);


--
-- Name: seats seats_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.seats
    ADD CONSTRAINT seats_pkey PRIMARY KEY (id);


--
-- Name: subscriptions subscriptions_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.subscriptions
    ADD CONSTRAINT subscriptions_pkey PRIMARY KEY (id);


--
-- Name: table_status_history table_status_history_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.table_status_history
    ADD CONSTRAINT table_status_history_pkey PRIMARY KEY (id);


--
-- Name: tables tables_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.tables
    ADD CONSTRAINT tables_pkey PRIMARY KEY (id);


--
-- Name: template_sections template_sections_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.template_sections
    ADD CONSTRAINT template_sections_pkey PRIMARY KEY (id);


--
-- Name: templates templates_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.templates
    ADD CONSTRAINT templates_pkey PRIMARY KEY (id);


--
-- Name: templates templates_slug_key; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.templates
    ADD CONSTRAINT templates_slug_key UNIQUE (slug);


--
-- Name: guest_groups uq_guest_groups_name; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.guest_groups
    ADD CONSTRAINT uq_guest_groups_name UNIQUE (wedding_id, name);


--
-- Name: guest_participants uq_guest_participants_id_guest; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.guest_participants
    ADD CONSTRAINT uq_guest_participants_id_guest UNIQUE (id, guest_id);


--
-- Name: guests uq_guests_guest_code; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.guests
    ADD CONSTRAINT uq_guests_guest_code UNIQUE (wedding_id, guest_code);


--
-- Name: invitations uq_invitations_guest; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.invitations
    ADD CONSTRAINT uq_invitations_guest UNIQUE (wedding_id, guest_id);


--
-- Name: seats uq_seats_number; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.seats
    ADD CONSTRAINT uq_seats_number UNIQUE (table_id, seat_number);


--
-- Name: tables uq_tables_number; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.tables
    ADD CONSTRAINT uq_tables_number UNIQUE (wedding_id, table_number);


--
-- Name: template_sections uq_template_sections; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.template_sections
    ADD CONSTRAINT uq_template_sections UNIQUE (template_id, section_key);


--
-- Name: wedding_members uq_wedding_members; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.wedding_members
    ADD CONSTRAINT uq_wedding_members UNIQUE (wedding_id, user_id);


--
-- Name: user_claims user_claims_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.user_claims
    ADD CONSTRAINT user_claims_pkey PRIMARY KEY (id);


--
-- Name: user_logins user_logins_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.user_logins
    ADD CONSTRAINT user_logins_pkey PRIMARY KEY (login_provider, provider_key);


--
-- Name: user_roles user_roles_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.user_roles
    ADD CONSTRAINT user_roles_pkey PRIMARY KEY (user_id, role_id);


--
-- Name: user_tokens user_tokens_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.user_tokens
    ADD CONSTRAINT user_tokens_pkey PRIMARY KEY (user_id, login_provider, name);


--
-- Name: users users_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.users
    ADD CONSTRAINT users_pkey PRIMARY KEY (id);


--
-- Name: venues venues_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.venues
    ADD CONSTRAINT venues_pkey PRIMARY KEY (id);


--
-- Name: waitlist_entries waitlist_entries_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.waitlist_entries
    ADD CONSTRAINT waitlist_entries_pkey PRIMARY KEY (id);


--
-- Name: walk_ins walk_ins_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.walk_ins
    ADD CONSTRAINT walk_ins_pkey PRIMARY KEY (id);


--
-- Name: wedding_events wedding_events_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.wedding_events
    ADD CONSTRAINT wedding_events_pkey PRIMARY KEY (id);


--
-- Name: wedding_media wedding_media_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.wedding_media
    ADD CONSTRAINT wedding_media_pkey PRIMARY KEY (id);


--
-- Name: wedding_member_permissions wedding_member_permissions_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.wedding_member_permissions
    ADD CONSTRAINT wedding_member_permissions_pkey PRIMARY KEY (member_id, permission_id);


--
-- Name: wedding_members wedding_members_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.wedding_members
    ADD CONSTRAINT wedding_members_pkey PRIMARY KEY (id);


--
-- Name: wedding_settings wedding_settings_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.wedding_settings
    ADD CONSTRAINT wedding_settings_pkey PRIMARY KEY (wedding_id);


--
-- Name: weddings weddings_pkey; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.weddings
    ADD CONSTRAINT weddings_pkey PRIMARY KEY (id);


--
-- Name: weddings weddings_slug_key; Type: CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.weddings
    ADD CONSTRAINT weddings_slug_key UNIQUE (slug);


--
-- Name: ix_ai_generations_type; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_ai_generations_type ON inviteme.ai_generations USING btree (type);


--
-- Name: ix_ai_generations_wedding_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_ai_generations_wedding_id ON inviteme.ai_generations USING btree (wedding_id);


--
-- Name: ix_audit_logs_created_at; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_audit_logs_created_at ON inviteme.audit_logs USING btree (created_at);


--
-- Name: ix_audit_logs_entity; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_audit_logs_entity ON inviteme.audit_logs USING btree (entity_type, entity_id);


--
-- Name: ix_audit_logs_wedding_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_audit_logs_wedding_id ON inviteme.audit_logs USING btree (wedding_id);


--
-- Name: ix_checkins_checked_in_at; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_checkins_checked_in_at ON inviteme.check_ins USING btree (checked_in_at);


--
-- Name: ix_checkins_wedding_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_checkins_wedding_id ON inviteme.check_ins USING btree (wedding_id);


--
-- Name: ix_gifts_guest_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_gifts_guest_id ON inviteme.gifts USING btree (guest_id);


--
-- Name: ix_gifts_received_at; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_gifts_received_at ON inviteme.gifts USING btree (received_at);


--
-- Name: ix_gifts_wedding_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_gifts_wedding_id ON inviteme.gifts USING btree (wedding_id);


--
-- Name: ix_guest_groups_wedding_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_guest_groups_wedding_id ON inviteme.guest_groups USING btree (wedding_id);


--
-- Name: ix_guest_notes_guest_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_guest_notes_guest_id ON inviteme.guest_notes USING btree (guest_id);


--
-- Name: ix_guest_notes_participant_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_guest_notes_participant_id ON inviteme.guest_notes USING btree (participant_id);


--
-- Name: ix_guest_participants_attendance; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_guest_participants_attendance ON inviteme.guest_participants USING btree (attendance_status);


--
-- Name: ix_guest_participants_guest_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_guest_participants_guest_id ON inviteme.guest_participants USING btree (guest_id);


--
-- Name: ix_guests_email_lower; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_guests_email_lower ON inviteme.guests USING btree (lower((email)::text));


--
-- Name: ix_guests_group_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_guests_group_id ON inviteme.guests USING btree (group_id);


--
-- Name: ix_guests_phone; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_guests_phone ON inviteme.guests USING btree (phone);


--
-- Name: ix_guests_wedding_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_guests_wedding_id ON inviteme.guests USING btree (wedding_id);


--
-- Name: ix_invitation_deliveries_invitation_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_invitation_deliveries_invitation_id ON inviteme.invitation_deliveries USING btree (invitation_id);


--
-- Name: ix_invitation_deliveries_status; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_invitation_deliveries_status ON inviteme.invitation_deliveries USING btree (status);


--
-- Name: ix_invitations_guest_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_invitations_guest_id ON inviteme.invitations USING btree (guest_id);


--
-- Name: ix_invitations_wedding_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_invitations_wedding_id ON inviteme.invitations USING btree (wedding_id);


--
-- Name: ix_love_stories_wedding_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_love_stories_wedding_id ON inviteme.love_stories USING btree (wedding_id);


--
-- Name: ix_notifications_recipient_guest; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_notifications_recipient_guest ON inviteme.notifications USING btree (recipient_guest_id);


--
-- Name: ix_notifications_recipient_user; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_notifications_recipient_user ON inviteme.notifications USING btree (recipient_user_id);


--
-- Name: ix_notifications_status_schedule; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_notifications_status_schedule ON inviteme.notifications USING btree (status, scheduled_at);


--
-- Name: ix_notifications_wedding_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_notifications_wedding_id ON inviteme.notifications USING btree (wedding_id);


--
-- Name: ix_role_claims_role_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_role_claims_role_id ON inviteme.role_claims USING btree (role_id);


--
-- Name: ix_role_permissions_permission_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_role_permissions_permission_id ON inviteme.role_permissions USING btree (permission_id);


--
-- Name: ix_rsvp_history_changed_at; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_rsvp_history_changed_at ON inviteme.rsvp_history USING btree (changed_at);


--
-- Name: ix_rsvp_history_rsvp_changed_at; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_rsvp_history_rsvp_changed_at ON inviteme.rsvp_history USING btree (rsvp_id, changed_at);


--
-- Name: ix_seating_assignments_table_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_seating_assignments_table_id ON inviteme.seating_assignments USING btree (table_id);


--
-- Name: ix_seating_assignments_wedding_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_seating_assignments_wedding_id ON inviteme.seating_assignments USING btree (wedding_id);


--
-- Name: ix_seating_change_logs_created_at; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_seating_change_logs_created_at ON inviteme.seating_change_logs USING btree (created_at);


--
-- Name: ix_seating_change_logs_participant_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_seating_change_logs_participant_id ON inviteme.seating_change_logs USING btree (participant_id);


--
-- Name: ix_seating_change_logs_wedding_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_seating_change_logs_wedding_id ON inviteme.seating_change_logs USING btree (wedding_id);


--
-- Name: ix_seats_table_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_seats_table_id ON inviteme.seats USING btree (table_id);


--
-- Name: ix_subscriptions_plan_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_subscriptions_plan_id ON inviteme.subscriptions USING btree (plan_id);


--
-- Name: ix_subscriptions_user_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_subscriptions_user_id ON inviteme.subscriptions USING btree (user_id);


--
-- Name: ix_table_status_history_table_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_table_status_history_table_id ON inviteme.table_status_history USING btree (table_id);


--
-- Name: ix_table_status_history_wedding_changed_at; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_table_status_history_wedding_changed_at ON inviteme.table_status_history USING btree (wedding_id, changed_at);


--
-- Name: ix_tables_wedding_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_tables_wedding_id ON inviteme.tables USING btree (wedding_id);


--
-- Name: ix_template_sections_template_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_template_sections_template_id ON inviteme.template_sections USING btree (template_id);


--
-- Name: ix_user_claims_user_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_user_claims_user_id ON inviteme.user_claims USING btree (user_id);


--
-- Name: ix_user_logins_user_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_user_logins_user_id ON inviteme.user_logins USING btree (user_id);


--
-- Name: ix_user_roles_role_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_user_roles_role_id ON inviteme.user_roles USING btree (role_id);


--
-- Name: ix_users_normalized_email; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_users_normalized_email ON inviteme.users USING btree (normalized_email);


--
-- Name: ix_venues_wedding_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_venues_wedding_id ON inviteme.venues USING btree (wedding_id);


--
-- Name: ix_waitlist_guest_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_waitlist_guest_id ON inviteme.waitlist_entries USING btree (guest_id);


--
-- Name: ix_waitlist_wedding_status; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_waitlist_wedding_status ON inviteme.waitlist_entries USING btree (wedding_id, status);


--
-- Name: ix_walkins_table_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_walkins_table_id ON inviteme.walk_ins USING btree (table_id);


--
-- Name: ix_walkins_wedding_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_walkins_wedding_id ON inviteme.walk_ins USING btree (wedding_id);


--
-- Name: ix_wedding_events_venue_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_wedding_events_venue_id ON inviteme.wedding_events USING btree (venue_id);


--
-- Name: ix_wedding_events_wedding_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_wedding_events_wedding_id ON inviteme.wedding_events USING btree (wedding_id);


--
-- Name: ix_wedding_media_wedding_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_wedding_media_wedding_id ON inviteme.wedding_media USING btree (wedding_id);


--
-- Name: ix_wedding_member_permissions_permission_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_wedding_member_permissions_permission_id ON inviteme.wedding_member_permissions USING btree (permission_id);


--
-- Name: ix_wedding_members_user_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_wedding_members_user_id ON inviteme.wedding_members USING btree (user_id);


--
-- Name: ix_wedding_members_wedding_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_wedding_members_wedding_id ON inviteme.wedding_members USING btree (wedding_id);


--
-- Name: ix_weddings_owner_user_id; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE INDEX ix_weddings_owner_user_id ON inviteme.weddings USING btree (owner_user_id);


--
-- Name: uq_checkins_participant; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE UNIQUE INDEX uq_checkins_participant ON inviteme.check_ins USING btree (wedding_id, participant_id) WHERE ((participant_id IS NOT NULL) AND ((status)::text = 'CHECKED_IN'::text));


--
-- Name: uq_checkins_walkin; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE UNIQUE INDEX uq_checkins_walkin ON inviteme.check_ins USING btree (wedding_id, walkin_id) WHERE ((walkin_id IS NOT NULL) AND ((status)::text = 'CHECKED_IN'::text));


--
-- Name: uq_gifts_idempotency; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE UNIQUE INDEX uq_gifts_idempotency ON inviteme.gifts USING btree (idempotency_key) WHERE (idempotency_key IS NOT NULL);


--
-- Name: uq_gifts_transaction; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE UNIQUE INDEX uq_gifts_transaction ON inviteme.gifts USING btree (wedding_id, provider, transaction_ref) WHERE ((provider IS NOT NULL) AND (transaction_ref IS NOT NULL));


--
-- Name: uq_guest_primary_participant; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE UNIQUE INDEX uq_guest_primary_participant ON inviteme.guest_participants USING btree (guest_id) WHERE ((participant_type)::text = 'PRIMARY'::text);


--
-- Name: uq_invitation_delivery_request; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE UNIQUE INDEX uq_invitation_delivery_request ON inviteme.invitation_deliveries USING btree (invitation_id, request_key) WHERE (request_key IS NOT NULL);


--
-- Name: uq_roles_normalized_name; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE UNIQUE INDEX uq_roles_normalized_name ON inviteme.roles USING btree (normalized_name);


--
-- Name: uq_seating_assignment_seat; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE UNIQUE INDEX uq_seating_assignment_seat ON inviteme.seating_assignments USING btree (seat_id) WHERE (seat_id IS NOT NULL);


--
-- Name: uq_users_email_lower; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE UNIQUE INDEX uq_users_email_lower ON inviteme.users USING btree (lower((email)::text));


--
-- Name: uq_users_normalized_user_name; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE UNIQUE INDEX uq_users_normalized_user_name ON inviteme.users USING btree (normalized_user_name);


--
-- Name: uq_waitlist_active_participant; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE UNIQUE INDEX uq_waitlist_active_participant ON inviteme.waitlist_entries USING btree (wedding_id, participant_id) WHERE ((participant_id IS NOT NULL) AND ((status)::text = 'WAITING'::text));


--
-- Name: uq_waitlist_active_party; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE UNIQUE INDEX uq_waitlist_active_party ON inviteme.waitlist_entries USING btree (wedding_id, guest_id) WHERE ((participant_id IS NULL) AND ((status)::text = 'WAITING'::text));


--
-- Name: uq_wedding_main_event; Type: INDEX; Schema: inviteme; Owner: -
--

CREATE UNIQUE INDEX uq_wedding_main_event ON inviteme.wedding_events USING btree (wedding_id) WHERE is_main;


--
-- Name: gift_messages trg_gift_messages_updated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_gift_messages_updated_at BEFORE UPDATE ON inviteme.gift_messages FOR EACH ROW EXECUTE FUNCTION inviteme.set_updated_at();


--
-- Name: gifts trg_gifts_updated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_gifts_updated_at BEFORE UPDATE ON inviteme.gifts FOR EACH ROW EXECUTE FUNCTION inviteme.set_updated_at();


--
-- Name: guest_groups trg_guest_groups_updated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_guest_groups_updated_at BEFORE UPDATE ON inviteme.guest_groups FOR EACH ROW EXECUTE FUNCTION inviteme.set_updated_at();


--
-- Name: guest_notes trg_guest_notes_updated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_guest_notes_updated_at BEFORE UPDATE ON inviteme.guest_notes FOR EACH ROW EXECUTE FUNCTION inviteme.set_updated_at();


--
-- Name: guest_participants trg_guest_participants_updated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_guest_participants_updated_at BEFORE UPDATE ON inviteme.guest_participants FOR EACH ROW EXECUTE FUNCTION inviteme.set_updated_at();


--
-- Name: guests trg_guests_updated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_guests_updated_at BEFORE UPDATE ON inviteme.guests FOR EACH ROW EXECUTE FUNCTION inviteme.set_updated_at();


--
-- Name: invitations trg_invitations_updated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_invitations_updated_at BEFORE UPDATE ON inviteme.invitations FOR EACH ROW EXECUTE FUNCTION inviteme.set_updated_at();


--
-- Name: love_stories trg_love_stories_updated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_love_stories_updated_at BEFORE UPDATE ON inviteme.love_stories FOR EACH ROW EXECUTE FUNCTION inviteme.set_updated_at();


--
-- Name: notifications trg_notifications_updated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_notifications_updated_at BEFORE UPDATE ON inviteme.notifications FOR EACH ROW EXECUTE FUNCTION inviteme.set_updated_at();


--
-- Name: plans trg_plans_updated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_plans_updated_at BEFORE UPDATE ON inviteme.plans FOR EACH ROW EXECUTE FUNCTION inviteme.set_updated_at();


--
-- Name: rsvp_history trg_rsvp_history_append_only; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_rsvp_history_append_only BEFORE DELETE OR UPDATE ON inviteme.rsvp_history FOR EACH ROW EXECUTE FUNCTION inviteme.rsvp_history_append_only();


--
-- Name: rsvp_history trg_rsvp_history_no_truncate; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_rsvp_history_no_truncate BEFORE TRUNCATE ON inviteme.rsvp_history FOR EACH STATEMENT EXECUTE FUNCTION inviteme.rsvp_history_append_only();


--
-- Name: rsvps trg_rsvps_updated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_rsvps_updated_at BEFORE UPDATE ON inviteme.rsvps FOR EACH ROW EXECUTE FUNCTION inviteme.set_updated_at();


--
-- Name: seating_assignments trg_seating_assignments_updated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_seating_assignments_updated_at BEFORE UPDATE ON inviteme.seating_assignments FOR EACH ROW EXECUTE FUNCTION inviteme.set_updated_at();


--
-- Name: subscriptions trg_subscriptions_updated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_subscriptions_updated_at BEFORE UPDATE ON inviteme.subscriptions FOR EACH ROW EXECUTE FUNCTION inviteme.set_updated_at();


--
-- Name: tables trg_tables_set_activated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_tables_set_activated_at BEFORE INSERT OR UPDATE OF status ON inviteme.tables FOR EACH ROW EXECUTE FUNCTION inviteme.set_table_activated_at();


--
-- Name: tables trg_tables_updated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_tables_updated_at BEFORE UPDATE ON inviteme.tables FOR EACH ROW EXECUTE FUNCTION inviteme.set_updated_at();


--
-- Name: template_sections trg_template_sections_updated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_template_sections_updated_at BEFORE UPDATE ON inviteme.template_sections FOR EACH ROW EXECUTE FUNCTION inviteme.set_updated_at();


--
-- Name: templates trg_templates_updated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_templates_updated_at BEFORE UPDATE ON inviteme.templates FOR EACH ROW EXECUTE FUNCTION inviteme.set_updated_at();


--
-- Name: users trg_users_updated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_users_updated_at BEFORE UPDATE ON inviteme.users FOR EACH ROW EXECUTE FUNCTION inviteme.set_updated_at();


--
-- Name: check_ins trg_validate_checkin_scope; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_validate_checkin_scope BEFORE INSERT OR UPDATE OF wedding_id, participant_id, walkin_id ON inviteme.check_ins FOR EACH ROW EXECUTE FUNCTION inviteme.validate_checkin_scope();


--
-- Name: wedding_events trg_validate_event_venue_scope; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_validate_event_venue_scope BEFORE INSERT OR UPDATE OF wedding_id, venue_id ON inviteme.wedding_events FOR EACH ROW EXECUTE FUNCTION inviteme.validate_event_venue_scope();


--
-- Name: gifts trg_validate_gift_scope; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_validate_gift_scope BEFORE INSERT OR UPDATE OF wedding_id, guest_id, walkin_id ON inviteme.gifts FOR EACH ROW EXECUTE FUNCTION inviteme.validate_gift_scope();


--
-- Name: guests trg_validate_guest_group_scope; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_validate_guest_group_scope BEFORE INSERT OR UPDATE OF wedding_id, group_id ON inviteme.guests FOR EACH ROW EXECUTE FUNCTION inviteme.validate_guest_group_scope();


--
-- Name: invitations trg_validate_invitation_scope; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_validate_invitation_scope BEFORE INSERT OR UPDATE OF wedding_id, guest_id ON inviteme.invitations FOR EACH ROW EXECUTE FUNCTION inviteme.validate_invitation_scope();


--
-- Name: seating_assignments trg_validate_seating_assignment; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_validate_seating_assignment BEFORE INSERT OR UPDATE OF wedding_id, participant_id, table_id, seat_id ON inviteme.seating_assignments FOR EACH ROW EXECUTE FUNCTION inviteme.validate_seating_assignment();


--
-- Name: waitlist_entries trg_validate_waitlist_scope; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_validate_waitlist_scope BEFORE INSERT OR UPDATE OF wedding_id, guest_id, participant_id ON inviteme.waitlist_entries FOR EACH ROW EXECUTE FUNCTION inviteme.validate_waitlist_scope();


--
-- Name: walk_ins trg_validate_walkin; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_validate_walkin BEFORE INSERT OR UPDATE OF wedding_id, related_guest_id, table_id, party_size ON inviteme.walk_ins FOR EACH ROW EXECUTE FUNCTION inviteme.validate_walkin();


--
-- Name: venues trg_venues_updated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_venues_updated_at BEFORE UPDATE ON inviteme.venues FOR EACH ROW EXECUTE FUNCTION inviteme.set_updated_at();


--
-- Name: waitlist_entries trg_waitlist_entries_updated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_waitlist_entries_updated_at BEFORE UPDATE ON inviteme.waitlist_entries FOR EACH ROW EXECUTE FUNCTION inviteme.set_updated_at();


--
-- Name: walk_ins trg_walk_ins_updated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_walk_ins_updated_at BEFORE UPDATE ON inviteme.walk_ins FOR EACH ROW EXECUTE FUNCTION inviteme.set_updated_at();


--
-- Name: wedding_events trg_wedding_events_updated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_wedding_events_updated_at BEFORE UPDATE ON inviteme.wedding_events FOR EACH ROW EXECUTE FUNCTION inviteme.set_updated_at();


--
-- Name: wedding_media trg_wedding_media_updated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_wedding_media_updated_at BEFORE UPDATE ON inviteme.wedding_media FOR EACH ROW EXECUTE FUNCTION inviteme.set_updated_at();


--
-- Name: wedding_members trg_wedding_members_updated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_wedding_members_updated_at BEFORE UPDATE ON inviteme.wedding_members FOR EACH ROW EXECUTE FUNCTION inviteme.set_updated_at();


--
-- Name: wedding_settings trg_wedding_settings_updated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_wedding_settings_updated_at BEFORE UPDATE ON inviteme.wedding_settings FOR EACH ROW EXECUTE FUNCTION inviteme.set_updated_at();


--
-- Name: weddings trg_weddings_updated_at; Type: TRIGGER; Schema: inviteme; Owner: -
--

CREATE TRIGGER trg_weddings_updated_at BEFORE UPDATE ON inviteme.weddings FOR EACH ROW EXECUTE FUNCTION inviteme.set_updated_at();


--
-- Name: ai_generations fk_ai_generations_created_by; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.ai_generations
    ADD CONSTRAINT fk_ai_generations_created_by FOREIGN KEY (created_by) REFERENCES inviteme.users(id) ON DELETE SET NULL;


--
-- Name: ai_generations fk_ai_generations_wedding; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.ai_generations
    ADD CONSTRAINT fk_ai_generations_wedding FOREIGN KEY (wedding_id) REFERENCES inviteme.weddings(id) ON DELETE CASCADE;


--
-- Name: audit_logs fk_audit_logs_actor_guest; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.audit_logs
    ADD CONSTRAINT fk_audit_logs_actor_guest FOREIGN KEY (actor_guest_id) REFERENCES inviteme.guests(id) ON DELETE RESTRICT;


--
-- Name: audit_logs fk_audit_logs_actor_user; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.audit_logs
    ADD CONSTRAINT fk_audit_logs_actor_user FOREIGN KEY (actor_user_id) REFERENCES inviteme.users(id) ON DELETE RESTRICT;


--
-- Name: audit_logs fk_audit_logs_wedding; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.audit_logs
    ADD CONSTRAINT fk_audit_logs_wedding FOREIGN KEY (wedding_id) REFERENCES inviteme.weddings(id) ON DELETE SET NULL;


--
-- Name: check_ins fk_checkins_checked_in_by; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.check_ins
    ADD CONSTRAINT fk_checkins_checked_in_by FOREIGN KEY (checked_in_by) REFERENCES inviteme.users(id) ON DELETE RESTRICT;


--
-- Name: check_ins fk_checkins_participant; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.check_ins
    ADD CONSTRAINT fk_checkins_participant FOREIGN KEY (participant_id) REFERENCES inviteme.guest_participants(id) ON DELETE RESTRICT;


--
-- Name: check_ins fk_checkins_voided_by; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.check_ins
    ADD CONSTRAINT fk_checkins_voided_by FOREIGN KEY (voided_by) REFERENCES inviteme.users(id) ON DELETE SET NULL;


--
-- Name: check_ins fk_checkins_walkin; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.check_ins
    ADD CONSTRAINT fk_checkins_walkin FOREIGN KEY (walkin_id) REFERENCES inviteme.walk_ins(id) ON DELETE RESTRICT;


--
-- Name: check_ins fk_checkins_wedding; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.check_ins
    ADD CONSTRAINT fk_checkins_wedding FOREIGN KEY (wedding_id) REFERENCES inviteme.weddings(id) ON DELETE CASCADE;


--
-- Name: gift_messages fk_gift_messages_gift; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.gift_messages
    ADD CONSTRAINT fk_gift_messages_gift FOREIGN KEY (gift_id) REFERENCES inviteme.gifts(id) ON DELETE CASCADE;


--
-- Name: gifts fk_gifts_guest; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.gifts
    ADD CONSTRAINT fk_gifts_guest FOREIGN KEY (guest_id) REFERENCES inviteme.guests(id) ON DELETE RESTRICT;


--
-- Name: gifts fk_gifts_received_by; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.gifts
    ADD CONSTRAINT fk_gifts_received_by FOREIGN KEY (received_by) REFERENCES inviteme.users(id) ON DELETE SET NULL;


--
-- Name: gifts fk_gifts_walkin; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.gifts
    ADD CONSTRAINT fk_gifts_walkin FOREIGN KEY (walkin_id) REFERENCES inviteme.walk_ins(id) ON DELETE RESTRICT;


--
-- Name: gifts fk_gifts_wedding; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.gifts
    ADD CONSTRAINT fk_gifts_wedding FOREIGN KEY (wedding_id) REFERENCES inviteme.weddings(id) ON DELETE CASCADE;


--
-- Name: guest_groups fk_guest_groups_wedding; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.guest_groups
    ADD CONSTRAINT fk_guest_groups_wedding FOREIGN KEY (wedding_id) REFERENCES inviteme.weddings(id) ON DELETE CASCADE;


--
-- Name: guest_notes fk_guest_notes_created_by; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.guest_notes
    ADD CONSTRAINT fk_guest_notes_created_by FOREIGN KEY (created_by) REFERENCES inviteme.users(id) ON DELETE SET NULL;


--
-- Name: guest_notes fk_guest_notes_guest; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.guest_notes
    ADD CONSTRAINT fk_guest_notes_guest FOREIGN KEY (guest_id) REFERENCES inviteme.guests(id) ON DELETE CASCADE;


--
-- Name: guest_notes fk_guest_notes_participant_same_guest; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.guest_notes
    ADD CONSTRAINT fk_guest_notes_participant_same_guest FOREIGN KEY (participant_id, guest_id) REFERENCES inviteme.guest_participants(id, guest_id) ON DELETE CASCADE;


--
-- Name: guest_participants fk_guest_participants_guest; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.guest_participants
    ADD CONSTRAINT fk_guest_participants_guest FOREIGN KEY (guest_id) REFERENCES inviteme.guests(id) ON DELETE CASCADE;


--
-- Name: guests fk_guests_group; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.guests
    ADD CONSTRAINT fk_guests_group FOREIGN KEY (group_id) REFERENCES inviteme.guest_groups(id) ON DELETE SET NULL;


--
-- Name: guests fk_guests_wedding; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.guests
    ADD CONSTRAINT fk_guests_wedding FOREIGN KEY (wedding_id) REFERENCES inviteme.weddings(id) ON DELETE CASCADE;


--
-- Name: invitation_deliveries fk_invitation_deliveries_invitation; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.invitation_deliveries
    ADD CONSTRAINT fk_invitation_deliveries_invitation FOREIGN KEY (invitation_id) REFERENCES inviteme.invitations(id) ON DELETE CASCADE;


--
-- Name: invitations fk_invitations_approved_by; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.invitations
    ADD CONSTRAINT fk_invitations_approved_by FOREIGN KEY (approved_by) REFERENCES inviteme.users(id) ON DELETE SET NULL;


--
-- Name: invitations fk_invitations_guest; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.invitations
    ADD CONSTRAINT fk_invitations_guest FOREIGN KEY (guest_id) REFERENCES inviteme.guests(id) ON DELETE CASCADE;


--
-- Name: invitations fk_invitations_wedding; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.invitations
    ADD CONSTRAINT fk_invitations_wedding FOREIGN KEY (wedding_id) REFERENCES inviteme.weddings(id) ON DELETE CASCADE;


--
-- Name: love_stories fk_love_stories_wedding; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.love_stories
    ADD CONSTRAINT fk_love_stories_wedding FOREIGN KEY (wedding_id) REFERENCES inviteme.weddings(id) ON DELETE CASCADE;


--
-- Name: notifications fk_notifications_guest; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.notifications
    ADD CONSTRAINT fk_notifications_guest FOREIGN KEY (recipient_guest_id) REFERENCES inviteme.guests(id) ON DELETE CASCADE;


--
-- Name: notifications fk_notifications_user; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.notifications
    ADD CONSTRAINT fk_notifications_user FOREIGN KEY (recipient_user_id) REFERENCES inviteme.users(id) ON DELETE CASCADE;


--
-- Name: notifications fk_notifications_wedding; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.notifications
    ADD CONSTRAINT fk_notifications_wedding FOREIGN KEY (wedding_id) REFERENCES inviteme.weddings(id) ON DELETE CASCADE;


--
-- Name: role_claims fk_role_claims_role; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.role_claims
    ADD CONSTRAINT fk_role_claims_role FOREIGN KEY (role_id) REFERENCES inviteme.roles(id) ON DELETE CASCADE;


--
-- Name: role_permissions fk_role_permissions_permission; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.role_permissions
    ADD CONSTRAINT fk_role_permissions_permission FOREIGN KEY (permission_id) REFERENCES inviteme.permissions(id) ON DELETE CASCADE;


--
-- Name: role_permissions fk_role_permissions_role; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.role_permissions
    ADD CONSTRAINT fk_role_permissions_role FOREIGN KEY (role_id) REFERENCES inviteme.roles(id) ON DELETE CASCADE;


--
-- Name: rsvp_history fk_rsvp_history_actor_guest; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.rsvp_history
    ADD CONSTRAINT fk_rsvp_history_actor_guest FOREIGN KEY (actor_guest_id) REFERENCES inviteme.guests(id) ON DELETE RESTRICT;


--
-- Name: rsvp_history fk_rsvp_history_changed_by; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.rsvp_history
    ADD CONSTRAINT fk_rsvp_history_changed_by FOREIGN KEY (changed_by) REFERENCES inviteme.users(id) ON DELETE RESTRICT;


--
-- Name: rsvp_history fk_rsvp_history_rsvp; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.rsvp_history
    ADD CONSTRAINT fk_rsvp_history_rsvp FOREIGN KEY (rsvp_id) REFERENCES inviteme.rsvps(id) ON DELETE RESTRICT;


--
-- Name: rsvps fk_rsvps_invitation; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.rsvps
    ADD CONSTRAINT fk_rsvps_invitation FOREIGN KEY (invitation_id) REFERENCES inviteme.invitations(id) ON DELETE CASCADE;


--
-- Name: seating_assignments fk_seating_assignment_assigned_by; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.seating_assignments
    ADD CONSTRAINT fk_seating_assignment_assigned_by FOREIGN KEY (assigned_by) REFERENCES inviteme.users(id) ON DELETE RESTRICT;


--
-- Name: seating_assignments fk_seating_assignment_participant; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.seating_assignments
    ADD CONSTRAINT fk_seating_assignment_participant FOREIGN KEY (participant_id) REFERENCES inviteme.guest_participants(id) ON DELETE CASCADE;


--
-- Name: seating_assignments fk_seating_assignment_seat; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.seating_assignments
    ADD CONSTRAINT fk_seating_assignment_seat FOREIGN KEY (seat_id) REFERENCES inviteme.seats(id) ON DELETE SET NULL;


--
-- Name: seating_assignments fk_seating_assignment_table; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.seating_assignments
    ADD CONSTRAINT fk_seating_assignment_table FOREIGN KEY (table_id) REFERENCES inviteme.tables(id) ON DELETE CASCADE;


--
-- Name: seating_assignments fk_seating_assignment_wedding; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.seating_assignments
    ADD CONSTRAINT fk_seating_assignment_wedding FOREIGN KEY (wedding_id) REFERENCES inviteme.weddings(id) ON DELETE CASCADE;


--
-- Name: seating_change_logs fk_seating_logs_actor; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.seating_change_logs
    ADD CONSTRAINT fk_seating_logs_actor FOREIGN KEY (actor_id) REFERENCES inviteme.users(id) ON DELETE RESTRICT;


--
-- Name: seating_change_logs fk_seating_logs_assignment; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.seating_change_logs
    ADD CONSTRAINT fk_seating_logs_assignment FOREIGN KEY (assignment_id) REFERENCES inviteme.seating_assignments(id) ON DELETE SET NULL;


--
-- Name: seating_change_logs fk_seating_logs_from_seat; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.seating_change_logs
    ADD CONSTRAINT fk_seating_logs_from_seat FOREIGN KEY (from_seat_id) REFERENCES inviteme.seats(id) ON DELETE SET NULL;


--
-- Name: seating_change_logs fk_seating_logs_from_table; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.seating_change_logs
    ADD CONSTRAINT fk_seating_logs_from_table FOREIGN KEY (from_table_id) REFERENCES inviteme.tables(id) ON DELETE SET NULL;


--
-- Name: seating_change_logs fk_seating_logs_participant; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.seating_change_logs
    ADD CONSTRAINT fk_seating_logs_participant FOREIGN KEY (participant_id) REFERENCES inviteme.guest_participants(id) ON DELETE RESTRICT;


--
-- Name: seating_change_logs fk_seating_logs_to_seat; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.seating_change_logs
    ADD CONSTRAINT fk_seating_logs_to_seat FOREIGN KEY (to_seat_id) REFERENCES inviteme.seats(id) ON DELETE SET NULL;


--
-- Name: seating_change_logs fk_seating_logs_to_table; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.seating_change_logs
    ADD CONSTRAINT fk_seating_logs_to_table FOREIGN KEY (to_table_id) REFERENCES inviteme.tables(id) ON DELETE SET NULL;


--
-- Name: seating_change_logs fk_seating_logs_wedding; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.seating_change_logs
    ADD CONSTRAINT fk_seating_logs_wedding FOREIGN KEY (wedding_id) REFERENCES inviteme.weddings(id) ON DELETE CASCADE;


--
-- Name: seats fk_seats_table; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.seats
    ADD CONSTRAINT fk_seats_table FOREIGN KEY (table_id) REFERENCES inviteme.tables(id) ON DELETE CASCADE;


--
-- Name: subscriptions fk_subscriptions_plan; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.subscriptions
    ADD CONSTRAINT fk_subscriptions_plan FOREIGN KEY (plan_id) REFERENCES inviteme.plans(id) ON DELETE RESTRICT;


--
-- Name: subscriptions fk_subscriptions_user; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.subscriptions
    ADD CONSTRAINT fk_subscriptions_user FOREIGN KEY (user_id) REFERENCES inviteme.users(id) ON DELETE CASCADE;


--
-- Name: table_status_history fk_table_status_history_changed_by; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.table_status_history
    ADD CONSTRAINT fk_table_status_history_changed_by FOREIGN KEY (changed_by) REFERENCES inviteme.users(id) ON DELETE SET NULL;


--
-- Name: table_status_history fk_table_status_history_table; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.table_status_history
    ADD CONSTRAINT fk_table_status_history_table FOREIGN KEY (table_id) REFERENCES inviteme.tables(id) ON DELETE CASCADE;


--
-- Name: table_status_history fk_table_status_history_wedding; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.table_status_history
    ADD CONSTRAINT fk_table_status_history_wedding FOREIGN KEY (wedding_id) REFERENCES inviteme.weddings(id) ON DELETE CASCADE;


--
-- Name: tables fk_tables_wedding; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.tables
    ADD CONSTRAINT fk_tables_wedding FOREIGN KEY (wedding_id) REFERENCES inviteme.weddings(id) ON DELETE CASCADE;


--
-- Name: template_sections fk_template_sections_template; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.template_sections
    ADD CONSTRAINT fk_template_sections_template FOREIGN KEY (template_id) REFERENCES inviteme.templates(id) ON DELETE CASCADE;


--
-- Name: user_claims fk_user_claims_user; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.user_claims
    ADD CONSTRAINT fk_user_claims_user FOREIGN KEY (user_id) REFERENCES inviteme.users(id) ON DELETE CASCADE;


--
-- Name: user_logins fk_user_logins_user; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.user_logins
    ADD CONSTRAINT fk_user_logins_user FOREIGN KEY (user_id) REFERENCES inviteme.users(id) ON DELETE CASCADE;


--
-- Name: user_roles fk_user_roles_role; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.user_roles
    ADD CONSTRAINT fk_user_roles_role FOREIGN KEY (role_id) REFERENCES inviteme.roles(id) ON DELETE CASCADE;


--
-- Name: user_roles fk_user_roles_user; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.user_roles
    ADD CONSTRAINT fk_user_roles_user FOREIGN KEY (user_id) REFERENCES inviteme.users(id) ON DELETE CASCADE;


--
-- Name: user_tokens fk_user_tokens_user; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.user_tokens
    ADD CONSTRAINT fk_user_tokens_user FOREIGN KEY (user_id) REFERENCES inviteme.users(id) ON DELETE CASCADE;


--
-- Name: venues fk_venues_wedding; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.venues
    ADD CONSTRAINT fk_venues_wedding FOREIGN KEY (wedding_id) REFERENCES inviteme.weddings(id) ON DELETE CASCADE;


--
-- Name: waitlist_entries fk_waitlist_guest; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.waitlist_entries
    ADD CONSTRAINT fk_waitlist_guest FOREIGN KEY (guest_id) REFERENCES inviteme.guests(id) ON DELETE CASCADE;


--
-- Name: waitlist_entries fk_waitlist_participant; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.waitlist_entries
    ADD CONSTRAINT fk_waitlist_participant FOREIGN KEY (participant_id) REFERENCES inviteme.guest_participants(id) ON DELETE SET NULL;


--
-- Name: waitlist_entries fk_waitlist_wedding; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.waitlist_entries
    ADD CONSTRAINT fk_waitlist_wedding FOREIGN KEY (wedding_id) REFERENCES inviteme.weddings(id) ON DELETE CASCADE;


--
-- Name: walk_ins fk_walkins_created_by; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.walk_ins
    ADD CONSTRAINT fk_walkins_created_by FOREIGN KEY (created_by) REFERENCES inviteme.users(id) ON DELETE RESTRICT;


--
-- Name: walk_ins fk_walkins_related_guest; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.walk_ins
    ADD CONSTRAINT fk_walkins_related_guest FOREIGN KEY (related_guest_id) REFERENCES inviteme.guests(id) ON DELETE SET NULL;


--
-- Name: walk_ins fk_walkins_table; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.walk_ins
    ADD CONSTRAINT fk_walkins_table FOREIGN KEY (table_id) REFERENCES inviteme.tables(id) ON DELETE SET NULL;


--
-- Name: walk_ins fk_walkins_wedding; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.walk_ins
    ADD CONSTRAINT fk_walkins_wedding FOREIGN KEY (wedding_id) REFERENCES inviteme.weddings(id) ON DELETE CASCADE;


--
-- Name: wedding_events fk_wedding_events_venue; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.wedding_events
    ADD CONSTRAINT fk_wedding_events_venue FOREIGN KEY (venue_id) REFERENCES inviteme.venues(id) ON DELETE SET NULL;


--
-- Name: wedding_events fk_wedding_events_wedding; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.wedding_events
    ADD CONSTRAINT fk_wedding_events_wedding FOREIGN KEY (wedding_id) REFERENCES inviteme.weddings(id) ON DELETE CASCADE;


--
-- Name: wedding_media fk_wedding_media_wedding; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.wedding_media
    ADD CONSTRAINT fk_wedding_media_wedding FOREIGN KEY (wedding_id) REFERENCES inviteme.weddings(id) ON DELETE CASCADE;


--
-- Name: wedding_members fk_wedding_members_user; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.wedding_members
    ADD CONSTRAINT fk_wedding_members_user FOREIGN KEY (user_id) REFERENCES inviteme.users(id) ON DELETE CASCADE;


--
-- Name: wedding_members fk_wedding_members_wedding; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.wedding_members
    ADD CONSTRAINT fk_wedding_members_wedding FOREIGN KEY (wedding_id) REFERENCES inviteme.weddings(id) ON DELETE CASCADE;


--
-- Name: wedding_settings fk_wedding_settings_wedding; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.wedding_settings
    ADD CONSTRAINT fk_wedding_settings_wedding FOREIGN KEY (wedding_id) REFERENCES inviteme.weddings(id) ON DELETE CASCADE;


--
-- Name: weddings fk_weddings_owner; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.weddings
    ADD CONSTRAINT fk_weddings_owner FOREIGN KEY (owner_user_id) REFERENCES inviteme.users(id) ON DELETE RESTRICT;


--
-- Name: wedding_member_permissions fk_wmp_member; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.wedding_member_permissions
    ADD CONSTRAINT fk_wmp_member FOREIGN KEY (member_id) REFERENCES inviteme.wedding_members(id) ON DELETE CASCADE;


--
-- Name: wedding_member_permissions fk_wmp_permission; Type: FK CONSTRAINT; Schema: inviteme; Owner: -
--

ALTER TABLE ONLY inviteme.wedding_member_permissions
    ADD CONSTRAINT fk_wmp_permission FOREIGN KEY (permission_id) REFERENCES inviteme.permissions(id) ON DELETE CASCADE;


--
-- PostgreSQL database dump complete
--



-- ============================================================
-- Seed rows (roles, permissions, default template)
-- ============================================================
SET search_path TO inviteme, public;
INSERT INTO inviteme.permissions VALUES ('dc667e0e-8d74-41fc-a9cd-e4ac6c9fa64f', 'WEDDING_VIEW', 'View wedding', NULL, 'WEDDING', '2026-10-11 17:46:01.799018+07');
INSERT INTO inviteme.permissions VALUES ('e84cc136-5bf4-4ecb-844e-0f065470536a', 'WEDDING_EDIT', 'Edit wedding', NULL, 'WEDDING', '2026-10-11 17:46:01.799018+07');
INSERT INTO inviteme.permissions VALUES ('01125f8f-bb37-42c4-8aa7-31bb8d74465b', 'GUEST_VIEW', 'View guests', NULL, 'WEDDING', '2026-10-11 17:46:01.799018+07');
INSERT INTO inviteme.permissions VALUES ('2aaf6cbf-ed00-42a1-9f65-81fb0b48c0e5', 'GUEST_EDIT', 'Edit guests', NULL, 'WEDDING', '2026-10-11 17:46:01.799018+07');
INSERT INTO inviteme.permissions VALUES ('eb503103-57ff-4dc8-a62c-52ebb2b976a3', 'INVITATION_SEND', 'Send invitations', NULL, 'WEDDING', '2026-10-11 17:46:01.799018+07');
INSERT INTO inviteme.permissions VALUES ('6fbc8cc8-668a-40fa-8016-912a2357b36c', 'RSVP_VIEW', 'View RSVP', NULL, 'WEDDING', '2026-10-11 17:46:01.799018+07');
INSERT INTO inviteme.permissions VALUES ('6fe07754-e54d-4112-b4bd-984b0a19a11c', 'SEATING_VIEW', 'View seating chart', NULL, 'WEDDING', '2026-10-11 17:46:01.799018+07');
INSERT INTO inviteme.permissions VALUES ('98882f62-cb0d-43a0-be8b-721375c5168e', 'SEATING_EDIT', 'Edit seating chart', NULL, 'WEDDING', '2026-10-11 17:46:01.799018+07');
INSERT INTO inviteme.permissions VALUES ('2168108b-7bc9-4294-9d44-036cb43613d2', 'CHECKIN_MANAGE', 'Manage event check-in', NULL, 'WEDDING', '2026-10-11 17:46:01.799018+07');
INSERT INTO inviteme.permissions VALUES ('668cdb02-d1f3-43a4-8306-967e33cf5078', 'GIFT_VIEW', 'View gifts', NULL, 'WEDDING', '2026-10-11 17:46:01.799018+07');
INSERT INTO inviteme.permissions VALUES ('26f0656c-54b3-421f-829a-b1c2d52de250', 'GIFT_EDIT', 'Manage gifts', NULL, 'WEDDING', '2026-10-11 17:46:01.799018+07');
INSERT INTO inviteme.permissions VALUES ('17397d91-b087-4769-9e5b-9fe0586078bf', 'ANALYTICS_VIEW', 'View analytics', NULL, 'WEDDING', '2026-10-11 17:46:01.799018+07');
INSERT INTO inviteme.permissions VALUES ('1905d399-8cec-4dc3-a649-eaf30b40b369', 'PLATFORM_MANAGE', 'Manage platform', NULL, 'PLATFORM', '2026-10-11 17:46:01.799018+07');
INSERT INTO inviteme.roles VALUES ('392014ea-89a9-46d1-9f2c-02b30e904c2a', 'ADMIN', 'Administrator', 'Platform administrator', '2026-10-11 17:46:01.799018+07', 'ADMIN', '0bc7136e-d068-4094-9f2a-1252e7f8d0f9');
INSERT INTO inviteme.roles VALUES ('86379e6a-cff5-424f-ad46-732f38f84a96', 'USER', 'User', 'Standard InviteMe account', '2026-10-11 17:46:01.799018+07', 'USER', 'addc6623-0dec-4d5c-a68b-9c4941ed574c');
INSERT INTO inviteme.templates VALUES ('d1b3efea-8c1c-4d0a-802a-fae8485c3cf0', 'InviteMe Classic', 'inviteme-classic', 'classic', NULL, '{"presetId": "classic", "schemaVersion": 1}', 'ACTIVE', '2026-10-11 17:46:02.107582+07', '2026-10-11 17:46:02.107582+07');

COMMIT;
