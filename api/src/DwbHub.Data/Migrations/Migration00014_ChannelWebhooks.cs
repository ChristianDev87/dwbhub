using FluentMigrator;

namespace DwbHub.Data.Migrations;

[Migration(14, "Create channel_webhooks table (AES-256-GCM encrypted webhook tokens, Plan 1.0)")]
public sealed class Migration00014_ChannelWebhooks : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("014_channel_webhooks.sql");
    }
}
