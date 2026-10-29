using PackCheck.Servers;

namespace PackCheck.Tests;

public class ServerLimitsTests
{
    private static ServerDefinition Def(int cores) => new()
    {
        Id = "x", Name = "x", Directory = "x", Platform = ServerPlatform.Paper, MinecraftVersion = "1.21.1", JarFile = "paper.jar", JavaPath = "java", CpuCores = cores,
    };

    [Fact]
    public void CpuLimit_SetsJvmProcessorCount()
    {
        var args = ServerProcess.BuildArguments(Def(1));
        Assert.Contains("-XX:ActiveProcessorCount=1", args);
    }

    [Fact]
    public void NoCpuLimit_LeavesJvmDefault()
    {
        Assert.DoesNotContain(ServerProcess.BuildArguments(Def(0)), a => a.StartsWith("-XX:ActiveProcessorCount", StringComparison.Ordinal));
    }
}
