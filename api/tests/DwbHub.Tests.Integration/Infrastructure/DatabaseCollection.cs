using Xunit;

namespace DwbHub.Tests.Integration.Infrastructure;

/// <summary>
/// Apply [Collection(DatabaseCollection.Name)] on every integration-test class
/// that needs the shared Postgres container.
/// </summary>
[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "PostgresDatabase";
}
