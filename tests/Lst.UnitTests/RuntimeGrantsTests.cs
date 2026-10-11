using System.Text.RegularExpressions;
using Lst.Platform.Persistence;

namespace Lst.UnitTests;

public sealed class RuntimeGrantsTests
{
    [Fact]
    public void Grants_script_is_embedded_and_role_is_quoted()
    {
        var sql = RuntimeGrants.Sql("lst_app");
        Assert.DoesNotContain("{{runtime_role}}", sql, StringComparison.Ordinal);
        Assert.Contains("TO \"lst_app\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_table_gets_only_select_and_insert()
    {
        var sql = RuntimeGrants.Sql("lst_app");
        var auditGrants = Regex.Matches(sql, @"GRANT ([A-Z, ]+) ON platform\.audit_entries", RegexOptions.CultureInvariant);
        var grant = Assert.Single(auditGrants);
        Assert.Equal("SELECT, INSERT", grant.Groups[1].Value.Trim());
        Assert.DoesNotMatch(@"ON ALL TABLES IN SCHEMA[^;]*\bplatform\b", sql);
    }

    [Theory]
    [InlineData("lst_app; DROP TABLE x")]
    [InlineData("LstApp")]
    [InlineData("\"lst\"")]
    [InlineData("")]
    public void Unsafe_role_names_are_rejected(string role) =>
        Assert.Throws<ArgumentException>(() => RuntimeGrants.Sql(role));
}
