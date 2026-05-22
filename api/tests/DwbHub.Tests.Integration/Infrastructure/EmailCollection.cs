using Xunit;

namespace DwbHub.Tests.Integration.Infrastructure;

[CollectionDefinition(Name)]
public sealed class EmailCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<MailpitFixture>
{
    public const string Name = "EmailIntegrationCollection";
}
