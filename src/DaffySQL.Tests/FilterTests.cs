using DaffySQL.Client;

namespace DaffySQL.Tests;

/// <summary>
/// daffy_scan's filter goes inside the SELECT's WHERE (...), so it must be one condition: one that
/// closes the parenthesis, ends the statement or comments the rest away could add T-SQL of its own.
/// Anything inside a 'string', a [name] or a "name" is the string's or the name's.
/// </summary>
public class FilterTests
{
    [Theory]
    [InlineData("[Total] > 100 AND [Status] = 'open'")]
    [InlineData("[Id] IN (1, 2, 3) OR ([Region] = N'North' AND [Total] BETWEEN 1 AND 9)")]
    [InlineData("[Name] = 'it''s ) ; -- /* not code'")]
    [InlineData("[odd ;)]] name] = 1")]
    [InlineData("\"quoted ) name\" IS NULL")]
    [InlineData("EXISTS (SELECT 1 FROM [dbo].[t] WHERE ([a] = 1))")]
    [InlineData("[a] - 1 > 0 AND [b] / 2 < 3")]
    public void OneConditionIsTaken(string filter) => Assert.Equal(filter, SqlNames.Condition(filter));

    [Theory]
    [InlineData("1=1) DELETE FROM [dbo].[t] WHERE (1=1", "closes a parenthesis it did not open")]
    [InlineData("1=1) OR (1=1", "closes a parenthesis it did not open")]
    [InlineData("1=1; DROP TABLE [dbo].[t]", "has a ; in it")]
    [InlineData("1=1 -- the rest", "has a comment in it")]
    [InlineData("1=1 /* the rest */", "has a comment in it")]
    [InlineData("([a] = 1", "leaves a parenthesis open")]
    [InlineData("[a] = 'not closed", "a string is not closed")]
    [InlineData("[not closed = 1", "a name is not closed")]
    public void MoreThanOneIsRefused(string filter, string why)
    {
        var error = Assert.Throws<ArgumentException>(() => SqlNames.Condition(filter));
        Assert.Contains(why, error.Message);
        Assert.Contains("one T-SQL condition", error.Message);
    }
}
