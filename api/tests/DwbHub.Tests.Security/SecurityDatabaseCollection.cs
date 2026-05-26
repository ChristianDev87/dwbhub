using DwbHub.Tests.Integration.Infrastructure;
using Xunit;

namespace DwbHub.Tests.Security;

/// <summary>
/// Collection fixture that provides a shared Postgres container for security tests
/// that require a running database. The fixture itself is reused from
/// DwbHub.Tests.Integration.Infrastructure so both test assemblies run against
/// the same migration set.
///
/// Apply [Collection(SecurityDatabaseCollection.Name)] on any security test class
/// that depends on the Postgres fixture.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SecurityDatabaseCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "SecurityPostgresDatabase";
}
