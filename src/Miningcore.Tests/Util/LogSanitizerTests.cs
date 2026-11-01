using Miningcore.Util;
using Xunit;

namespace Miningcore.Tests.Util;

public class LogSanitizerTests
{
    [Fact]
    public void Name_Keeps_Address_Characters_Drops_Control_And_Truncates()
    {
        Assert.Equal("mib4FH9sizBpTveP2Shf3Rpxf4VqsWNcQC.rig-1", LogSanitizer.Name("mib4FH9sizBpTveP2Shf3Rpxf4VqsWNcQC.rig-1"));
        Assert.Equal("xet:abc", LogSanitizer.Name("xet:abc"));
        Assert.Equal("forgedIline", LogSanitizer.Name("forged\r\n[I] line"));   // CR/LF, brackets and spaces vanish
        Assert.Equal(string.Empty, LogSanitizer.Name(null));
        Assert.Equal(81, LogSanitizer.Name(new string('a', 500)).Length);      // 80 + the ellipsis
    }

    [Fact]
    public void WorkerName_Accepts_Plain_Names_Only()
    {
        Assert.Equal("rig1", LogSanitizer.WorkerName("rig1"));
        Assert.Equal("gpu_02-b", LogSanitizer.WorkerName("gpu_02-b"));
        Assert.Equal(string.Empty, LogSanitizer.WorkerName(""));
        Assert.Equal(string.Empty, LogSanitizer.WorkerName("a b"));
        Assert.Equal(string.Empty, LogSanitizer.WorkerName("<script>"));
        Assert.Equal(string.Empty, LogSanitizer.WorkerName(new string('w', 33)));
    }
}
