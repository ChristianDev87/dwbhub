using FluentMigrator;

namespace DwbHub.Data.Migrations;

/// <summary>
/// Adds a <c>public_id</c> UUID column to the <c>messages</c> table so that
/// user-facing PATCH / DELETE endpoints can address individual messages via a
/// stable, non-enumerable external key rather than the internal BIGINT PK.
/// Back-fills existing rows with random UUIDs automatically.
/// </summary>
[Migration(17, "Add public_id UUID to messages table (non-enumerable external key for PATCH/DELETE endpoints)")]
public sealed class Migration00017_MessagesPublicId : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("017_messages_public_id.sql");
    }
}
