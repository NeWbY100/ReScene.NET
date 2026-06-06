using ReScene.NET.Models;
using ReScene.NET.Services;

namespace ReScene.NET.Tests;

public class AppSettingsModeTests
{
    [Fact]
    public void ResolveStartupMode_NoFile_DefaultsToBeginner()
        => Assert.Equal(UserMode.Beginner, AppSettingsService.ResolveStartupMode(settingsFileExisted: false, persistedMode: null));

    [Fact]
    public void ResolveStartupMode_ExistingFileWithoutMode_DefaultsToAdvanced()
        => Assert.Equal(UserMode.Advanced, AppSettingsService.ResolveStartupMode(settingsFileExisted: true, persistedMode: null));

    [Theory]
    [InlineData(UserMode.Beginner)]
    [InlineData(UserMode.Advanced)]
    public void ResolveStartupMode_PersistedValue_IsHonored(UserMode persisted)
        => Assert.Equal(persisted, AppSettingsService.ResolveStartupMode(settingsFileExisted: true, persistedMode: persisted));
}
