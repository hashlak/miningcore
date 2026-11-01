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
        Assert.Equal(string.Empty, LogSanitizer.WorkerName(null));

        // anything else becomes a stable w-<8 hex>: no raw text, rigs still distinguishable
        foreach(var raw in new[] { "a b", "<script>", new string('w', 33), "rig1\n", "جهاز-1" })
        {
            var mapped = LogSanitizer.WorkerName(raw);
            Assert.Matches("^w-[0-9a-f]{8}$", mapped);
            Assert.Equal(mapped, LogSanitizer.WorkerName(raw));
        }

        Assert.NotEqual(LogSanitizer.WorkerName("a b"), LogSanitizer.WorkerName("a  b"));
    }
}
