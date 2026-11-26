using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Talky.Data.Migrations
{
    /// <inheritdoc />
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-added, see docs/data-model.md. Talky assumes snapshot semantics for readers:
            // every read path (history paging, conversation list, presence) must never block behind a
            // writer, and no read path takes NOLOCK to get there. It has to run outside the migration
            // transaction, and it wants to be the only connection in the database, which is why the
            // migration runner takes its application lock in master instead of here.
            migrationBuilder.Sql(
                "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;",
                suppressTransaction: true);

            migrationBuilder.CreateTable(
                name: "attachments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    message_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    conversation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    uploader_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    object_key = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    file_name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    mime_type = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    scan_status = table.Column<byte>(type: "tinyint", nullable: false),
                    scanned_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    preview_key = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    width = table.Column<int>(type: "int", nullable: true),
                    height = table.Column<int>(type: "int", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attachments", x => x.id)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("ck_attachments_size", "[size_bytes] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    actor_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    actor_name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    target_type = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    target_id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ip_address = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    data = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_logs", x => x.id)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("ck_audit_logs_data_json", "[data] IS NULL OR ISJSON([data]) = 1");
                });

            migrationBuilder.CreateTable(
                name: "call_participants",
                columns: table => new
                {
                    call_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    state = table.Column<byte>(type: "tinyint", nullable: false),
                    device_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    invited_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    joined_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    left_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    duration_seconds = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_call_participants", x => new { x.call_id, x.user_id })
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateTable(
                name: "calls",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    kind = table.Column<byte>(type: "tinyint", nullable: false),
                    state = table.Column<byte>(type: "tinyint", nullable: false),
                    started_by = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    room_name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    started_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    answered_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ended_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    end_reason = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    participant_count = table.Column<int>(type: "int", nullable: false),
                    recording_key = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_calls", x => x.id)
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateTable(
                name: "client_errors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    device_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    occurred_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    received_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    platform = table.Column<byte>(type: "tinyint", nullable: false),
                    app_version = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    message = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    stack = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    context = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_client_errors", x => x.id)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("ck_client_errors_context_json", "[context] IS NULL OR ISJSON([context]) = 1");
                });

            migrationBuilder.CreateTable(
                name: "cluster_nodes",
                columns: table => new
                {
                    node_id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    host_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    grpc_port = table.Column<int>(type: "int", nullable: false),
                    version = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    roles = table.Column<int>(type: "int", nullable: false),
                    started_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cluster_nodes", x => x.node_id)
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateTable(
                name: "federation_peers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    domain = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    display_name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    endpoint = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    public_key = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    state = table.Column<byte>(type: "tinyint", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    last_contact_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_federation_peers", x => x.id)
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateTable(
                name: "identity_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    kind = table.Column<byte>(type: "tinyint", nullable: false),
                    name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    home_domain = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    config = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    enabled = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_sources", x => x.id)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("ck_identity_sources_config_json", "[config] IS NULL OR ISJSON([config]) = 1");
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    type = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    state = table.Column<byte>(type: "tinyint", nullable: false),
                    attempts = table.Column<int>(type: "int", nullable: false),
                    max_attempts = table.Column<int>(type: "int", nullable: false),
                    run_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    locked_by = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    locked_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    dedupe_key = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    last_error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    completed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.id)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("ck_jobs_attempts", "[attempts] >= 0 AND [max_attempts] >= 1");
                });

            migrationBuilder.CreateTable(
                name: "messages",
                columns: table => new
                {
                    conversation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    sender_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    kind = table.Column<byte>(type: "tinyint", nullable: false),
                    parent_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    body = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    metadata = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    client_msg_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    edited_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messages", x => new { x.conversation_id, x.seq })
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("ck_messages_metadata_json", "[metadata] IS NULL OR ISJSON([metadata]) = 1");
                });

            migrationBuilder.CreateTable(
                name: "metrics_1m",
                columns: table => new
                {
                    bucket = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    node_id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    count = table.Column<long>(type: "bigint", nullable: false),
                    sum = table.Column<double>(type: "float", nullable: false),
                    min = table.Column<double>(type: "float", nullable: false),
                    max = table.Column<double>(type: "float", nullable: false),
                    p50 = table.Column<double>(type: "float", nullable: true),
                    p95 = table.Column<double>(type: "float", nullable: true),
                    p99 = table.Column<double>(type: "float", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metrics_1m", x => new { x.bucket, x.node_id, x.name })
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateTable(
                name: "reactions",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    emoji = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reactions", x => new { x.message_id, x.user_id, x.emoji })
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateTable(
                name: "teams",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    home_domain = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    slug = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    description = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    is_private = table.Column<bool>(type: "bit", nullable: false),
                    archived = table.Column<bool>(type: "bit", nullable: false),
                    created_by = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teams", x => x.id)
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateTable(
                name: "thread_meta",
                columns: table => new
                {
                    root_message_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    reply_count = table.Column<int>(type: "int", nullable: false),
                    last_reply_seq = table.Column<long>(type: "bigint", nullable: true),
                    last_reply_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    participant_count = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_thread_meta", x => x.root_message_id)
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    identity_source_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    external_id = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    home_domain = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    user_name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    display_name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    avatar_key = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    title = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    department = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    state = table.Column<byte>(type: "tinyint", nullable: false),
                    is_org_admin = table.Column<bool>(type: "bit", nullable: false),
                    is_bot = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id)
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_users_identity_sources_identity_source_id",
                        column: x => x.identity_source_id,
                        principalTable: "identity_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    type = table.Column<byte>(type: "tinyint", nullable: false),
                    home_domain = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    team_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    slug = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    title = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    topic = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    last_seq = table.Column<long>(type: "bigint", nullable: false),
                    last_message_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    archived = table.Column<bool>(type: "bit", nullable: false),
                    created_by = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversations", x => x.id)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("ck_conversations_last_seq", "[last_seq] >= 0");
                    table.ForeignKey(
                        name: "FK_conversations_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    platform = table.Column<byte>(type: "tinyint", nullable: false),
                    name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    push_kind = table.Column<byte>(type: "tinyint", nullable: false),
                    push_token = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    app_version = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    last_seen_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_devices", x => x.id)
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_devices_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_members",
                columns: table => new
                {
                    team_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    role = table.Column<byte>(type: "tinyint", nullable: false),
                    joined_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_members", x => new { x.team_id, x.user_id })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_team_members_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_team_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "conversation_members",
                columns: table => new
                {
                    conversation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    role = table.Column<byte>(type: "tinyint", nullable: false),
                    last_read_seq = table.Column<long>(type: "bigint", nullable: false),
                    mention_count = table.Column<int>(type: "int", nullable: false),
                    muted = table.Column<bool>(type: "bit", nullable: false),
                    notify_level = table.Column<byte>(type: "tinyint", nullable: false),
                    joined_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    last_read_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_members", x => new { x.conversation_id, x.user_id })
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("ck_conversation_members_counters", "[last_read_seq] >= 0 AND [mention_count] >= 0");
                    table.ForeignKey(
                        name: "FK_conversation_members_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_conversation_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dm_pairs",
                columns: table => new
                {
                    conversation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_a = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_b = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dm_pairs", x => x.conversation_id)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("ck_dm_pairs_order", "[user_a] < [user_b]");
                    table.ForeignKey(
                        name: "FK_dm_pairs_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    device_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    token_hash = table.Column<string>(type: "nvarchar(88)", maxLength: 88, nullable: false),
                    issued_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ip_address = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    user_agent = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    revoked_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.id)
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_sessions_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_attachments_message_id",
                table: "attachments",
                column: "message_id",
                filter: "[message_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_attachments_object_key",
                table: "attachments",
                column: "object_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attachments_scan_status_created_at",
                table: "attachments",
                columns: new[] { "scan_status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_attachments_sha256",
                table: "attachments",
                column: "sha256");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_actor_id_at",
                table: "audit_logs",
                columns: new[] { "actor_id", "at" },
                filter: "[actor_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_at",
                table: "audit_logs",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_target_type_target_id_at",
                table: "audit_logs",
                columns: new[] { "target_type", "target_id", "at" },
                filter: "[target_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_call_participants_user_id_invited_at",
                table: "call_participants",
                columns: new[] { "user_id", "invited_at" });

            migrationBuilder.CreateIndex(
                name: "IX_calls_conversation_id_started_at",
                table: "calls",
                columns: new[] { "conversation_id", "started_at" },
                filter: "[conversation_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_calls_room_name",
                table: "calls",
                column: "room_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_calls_state",
                table: "calls",
                column: "state",
                filter: "[state] < 2");

            migrationBuilder.CreateIndex(
                name: "IX_client_errors_kind_received_at",
                table: "client_errors",
                columns: new[] { "kind", "received_at" });

            migrationBuilder.CreateIndex(
                name: "IX_client_errors_received_at",
                table: "client_errors",
                column: "received_at");

            migrationBuilder.CreateIndex(
                name: "IX_cluster_nodes_last_seen_at",
                table: "cluster_nodes",
                column: "last_seen_at");

            migrationBuilder.CreateIndex(
                name: "IX_conversation_members_user_id",
                table: "conversation_members",
                column: "user_id")
                .Annotation("SqlServer:Include", new[] { "last_read_seq", "mention_count", "muted", "notify_level" });

            migrationBuilder.CreateIndex(
                name: "IX_conversations_team_id",
                table: "conversations",
                column: "team_id",
                filter: "[team_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_team_id_slug",
                table: "conversations",
                columns: new[] { "team_id", "slug" },
                unique: true,
                filter: "[team_id] IS NOT NULL AND [slug] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_devices_push_token",
                table: "devices",
                column: "push_token",
                unique: true,
                filter: "[push_token] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_devices_user_id",
                table: "devices",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_dm_pairs_user_a_user_b",
                table: "dm_pairs",
                columns: new[] { "user_a", "user_b" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_federation_peers_domain",
                table: "federation_peers",
                column: "domain",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_identity_sources_name",
                table: "identity_sources",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_jobs_completed_at",
                table: "jobs",
                column: "completed_at",
                filter: "[completed_at] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_dedupe_key",
                table: "jobs",
                column: "dedupe_key",
                unique: true,
                filter: "[dedupe_key] IS NOT NULL AND [state] < 2");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_locked_at",
                table: "jobs",
                column: "locked_at",
                filter: "[state] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_state_run_at",
                table: "jobs",
                columns: new[] { "state", "run_at" },
                filter: "[state] = 0")
                .Annotation("SqlServer:Include", new[] { "type" });

            migrationBuilder.CreateIndex(
                name: "IX_messages_id",
                table: "messages",
                column: "id",
                unique: true)
                .Annotation("SqlServer:Clustered", false);

            migrationBuilder.CreateIndex(
                name: "IX_messages_parent_id_seq",
                table: "messages",
                columns: new[] { "parent_id", "seq" },
                filter: "[parent_id] IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "conversation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_messages_sender_id_client_msg_id",
                table: "messages",
                columns: new[] { "sender_id", "client_msg_id" },
                unique: true)
                .Annotation("SqlServer:Clustered", false);

            migrationBuilder.CreateIndex(
                name: "IX_metrics_1m_name_bucket",
                table: "metrics_1m",
                columns: new[] { "name", "bucket" });

            migrationBuilder.CreateIndex(
                name: "IX_sessions_device_id",
                table: "sessions",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_expires_at",
                table: "sessions",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_token_hash",
                table: "sessions",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sessions_user_id",
                table: "sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_members_user_id",
                table: "team_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_teams_home_domain_slug",
                table: "teams",
                columns: new[] { "home_domain", "slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_thread_meta_conversation_id_last_reply_at",
                table: "thread_meta",
                columns: new[] { "conversation_id", "last_reply_at" });

            migrationBuilder.CreateIndex(
                name: "IX_users_email",
                table: "users",
                column: "email",
                filter: "[email] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_users_home_domain_user_name",
                table: "users",
                columns: new[] { "home_domain", "user_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_identity_source_id_external_id",
                table: "users",
                columns: new[] { "identity_source_id", "external_id" },
                unique: true,
                filter: "[external_id] IS NOT NULL");

            // Hand-added, see docs/data-model.md. messages is by far the largest table and its rows
            // are highly repetitive within a page (same conversation_id, few distinct sender_ids and
            // kinds, near-identical timestamps), so page compression typically halves it: fewer pages
            // means fewer logical reads and more history per gigabyte of buffer pool. It costs a little
            // CPU per insert, which the append path can afford. ALTER INDEX ALL covers the clustered
            // index and the three nonclustered ones. Re-apply after any later migration that rebuilds
            // this table.
            migrationBuilder.Sql("ALTER INDEX ALL ON [messages] REBUILD WITH (DATA_COMPRESSION = PAGE);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attachments");

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "call_participants");

            migrationBuilder.DropTable(
                name: "calls");

            migrationBuilder.DropTable(
                name: "client_errors");

            migrationBuilder.DropTable(
                name: "cluster_nodes");

            migrationBuilder.DropTable(
                name: "conversation_members");

            migrationBuilder.DropTable(
                name: "dm_pairs");

            migrationBuilder.DropTable(
                name: "federation_peers");

            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropTable(
                name: "messages");

            migrationBuilder.DropTable(
                name: "metrics_1m");

            migrationBuilder.DropTable(
                name: "reactions");

            migrationBuilder.DropTable(
                name: "sessions");

            migrationBuilder.DropTable(
                name: "team_members");

            migrationBuilder.DropTable(
                name: "thread_meta");

            migrationBuilder.DropTable(
                name: "conversations");

            migrationBuilder.DropTable(
                name: "devices");

            migrationBuilder.DropTable(
                name: "teams");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "identity_sources");
        }
    }
}
