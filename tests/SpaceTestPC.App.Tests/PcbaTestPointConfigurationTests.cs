using SpaceTestPC.Core.Models;
using SpaceTestPC.Core.Services;
using Xunit;

namespace SpaceTestPC.App.Tests;

/// <summary>
/// The 32-channel limits live in appsettings.json (jxTvm.channels) so a new 测试表 can be
/// applied without rebuilding. These tests make sure the shipped config still binds and
/// still carries the ranges from 电压检测仪设置\测试表.csv.
/// </summary>
public sealed class PcbaTestPointConfigurationTests
{
    private static readonly AppConfiguration Configuration =
        new ConfigurationService().Load(Path.Combine(FindRepositoryRoot(), "SpaceTestPC.App", "appsettings.json"));

    [Fact]
    public void ShippedConfiguration_BindsAllThirtyTwoChannels()
    {
        Assert.Equal(32, Configuration.JxTvm.Channels.Count);
        Assert.Equal(Enumerable.Range(1, 32), Configuration.JxTvm.Channels.Select(channel => channel.Channel));
        Assert.All(Configuration.JxTvm.Channels, channel =>
        {
            Assert.False(string.IsNullOrWhiteSpace(channel.Name));
            Assert.True(channel.MinMv <= channel.MaxMv);
        });
    }

    [Theory]
    // 电压检测仪设置\测试表.csv rows: 设定阈值 ± 允许偏差, converted to mV.
    [InlineData(1, -100, 300)]
    [InlineData(4, 4900, 5500)]
    [InlineData(7, 0, 1400)]
    [InlineData(8, 11000, 13000)]
    [InlineData(9, 900, 1300)]
    [InlineData(10, -100, 300)]
    [InlineData(13, 0, 200)]
    [InlineData(16, 1600, 2000)]
    [InlineData(19, 4800, 5600)]
    [InlineData(24, 4200, 4800)]
    [InlineData(25, 600, 1000)]
    [InlineData(26, 350, 650)]
    [InlineData(31, 0, 400)]
    public void ShippedConfiguration_UsesCsvVoltageLimits(int channel, double minMv, double maxMv)
    {
        var spec = Assert.Single(Configuration.JxTvm.Channels, item => item.Channel == channel);
        Assert.Equal(minMv, spec.MinMv);
        Assert.Equal(maxMv, spec.MaxMv);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "SpaceTestPC.App")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
