using System.Text;
using ConnectAgent.Core;
using Xunit;

public class JavaPropertiesTests
{
    [Fact]
    public void SeparatorsCommentsAndBlankLines()
    {
        var p = JavaProperties.Parse("""
            # comment
            ! also a comment

            a=1
            b = 2
            c:3
            d 4
              e	=	5
            """);
        Assert.Equal("1", p["a"]);
        Assert.Equal("2", p["b"]);
        Assert.Equal("3", p["c"]);
        Assert.Equal("4", p["d"]);
        Assert.Equal("5", p["e"]);
        Assert.Equal(5, p.Count);
    }

    [Fact]
    public void ContinuationLinesAndEscapes()
    {
        var p = JavaProperties.Parse("list=a,\\\n    b,\\\r\n    c\nkey\\=with\\:colon=v\\tx\npath=C:\\\\Program Files\\\\X14\nu=\\u00e5\\u00E4\n");
        Assert.Equal("a,b,c", p["list"]);
        Assert.Equal("v\tx", p["key=with:colon"]);
        Assert.Equal(@"C:\Program Files\X14", p["path"]);
        Assert.Equal("åä", p["u"]);
    }

    [Fact]
    public void TrailingSpacesAreKeptLikeJava()
    {
        // app11 2026-10-16: a hand edit left a trailing space and ConnectStandalone exited.
        var p = JavaProperties.Parse("retriable.restart.connector.wait.ms=300000 \nempty=\nlone\n");
        Assert.Equal("300000 ", p["retriable.restart.connector.wait.ms"]);
        Assert.Equal("", p["empty"]);
        Assert.Equal("", p["lone"]);
    }

    [Fact]
    public void LoadReadsLatin1()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.Latin1.GetBytes("name=prod_lime\ndatabase.dbname=k\u00e4lla\n"));
            var p = JavaProperties.Load(path);
            Assert.Equal("källa", p["database.dbname"]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
