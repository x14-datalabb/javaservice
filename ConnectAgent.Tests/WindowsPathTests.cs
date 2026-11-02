using ConnectAgent.Core;
using Xunit;

public class WindowsPathTests
{
    [Theory]
    [InlineData(@"C:\Program Files\X14\Sorcerer", @"C:\Program Files\X14\Sorcerer\jdk\bin\java.exe", true)]
    [InlineData(@"C:\Program Files\X14\Sorcerer\", @"c:\program files\x14\sorcerer\JDK\bin\java.exe", true)]
    [InlineData(@"C:\Program Files\X14\Sorcerer", @"C:/Program Files/X14/Sorcerer/jdk/bin/java.exe", true)]
    [InlineData(@"C:\Program Files\X14\Sorcerer", @"\\?\C:\Program Files\X14\Sorcerer\jdk\bin\java.exe", true)]
    [InlineData(@"C:\Program Files\X14\Sorcerer", @"C:\Program Files\X14\Sorcerer2\jdk\bin\java.exe", false)]
    [InlineData(@"C:\Program Files\X14\Sorcerer", @"C:\Program Files\Eclipse Adoptium\jdk-17\bin\java.exe", false)]
    [InlineData(@"C:\Program Files\X14\Sorcerer", @"C:\Program Files\X14\Sorcerer", false)]
    public void IsUnder(string dir, string path, bool expected) => Assert.Equal(expected, WindowsPath.IsUnder(dir, path));

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData(@"C:", true)]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"\\server\share\", true)]
    [InlineData(@"C:\Program Files\X14\Sorcerer", false)]
    [InlineData(@"\\server\share\Sorcerer", false)]
    public void IsRoot(string dir, bool expected) => Assert.Equal(expected, WindowsPath.IsRoot(dir));
}
