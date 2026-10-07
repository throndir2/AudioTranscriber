using AudioTranscriber.Providers;
using Xunit;

namespace AudioTranscriber.Providers.Tests;

public sealed class HardwareAdvisorTests
{
    private static HardwareProfile Pc(double vramGb, bool nvidia = true, int cores = 16, double ramGb = 32) =>
        new("Test CPU", cores, ramGb, vramGb <= 0 ? [] : [new GpuInfo("Test GPU", vramGb, nvidia, nvidia)]);

    [Fact]
    public void SmallGpuGoesToTheLocalLlmAndTranscriptionRunsOnTheCpu()
    {
        var plan = HardwareAdvisor.Plan(Pc(8), localLlm: true);
        Assert.Equal(PlanDevice.Gpu, plan.LlmDevice);
        Assert.Equal("gemma4:e2b", plan.LlmModel);
        Assert.False(plan.ParakeetOnGpu);
        Assert.False(plan.WhisperOnGpu);
        Assert.True(plan.VramPlannedGb <= plan.UsableVramGb);
    }

    [Fact]
    public void LargerGpuFitsTheLlmAndParakeet()
    {
        var plan = HardwareAdvisor.Plan(Pc(16), localLlm: true);
        Assert.Equal("gemma4:12b", plan.LlmModel);
        Assert.True(plan.ParakeetOnGpu);
        Assert.True(plan.VramPlannedGb <= plan.UsableVramGb);
    }

    [Fact]
    public void HostedLlmLeavesTheGpuToTranscription()
    {
        var plan = HardwareAdvisor.Plan(Pc(8), localLlm: false);
        Assert.Equal(PlanDevice.Hosted, plan.LlmDevice);
        Assert.True(plan.ParakeetOnGpu);
        Assert.True(plan.WhisperOnGpu);
        Assert.Equal("large-v3-turbo", plan.WhisperModelId);
    }

    [Fact]
    public void NoGpuRunsEverythingOnTheCpuSizedToTheProcessor()
    {
        var weak = HardwareAdvisor.Plan(Pc(0, cores: 4, ramGb: 8), localLlm: true);
        Assert.Equal(PlanDevice.Hosted, weak.LlmDevice);
        Assert.False(weak.ParakeetOnGpu);
        Assert.Equal("base", weak.WhisperModelId);
        var strong = HardwareAdvisor.Plan(Pc(0, cores: 16, ramGb: 32), localLlm: true);
        Assert.Equal(PlanDevice.Cpu, strong.LlmDevice);
        Assert.Equal("large-v3-turbo", strong.WhisperModelId);
    }

    [Fact]
    public void NonNvidiaGpuNeverRunsParakeet()
    {
        var plan = HardwareAdvisor.Plan(Pc(16, nvidia: false), localLlm: false);
        Assert.False(plan.ParakeetOnGpu);
        Assert.True(plan.WhisperOnGpu);
    }

    [Fact]
    public async Task ProbeReadsThisPc()
    {
        var hardware = await HardwareProbe.ProbeAsync();
        Assert.True(hardware.LogicalCores > 0 && hardware.RamGb > 0);
        Console.WriteLine(hardware.Describe());
        Console.WriteLine(HardwareAdvisor.Plan(hardware, true).Summary);
    }
}
