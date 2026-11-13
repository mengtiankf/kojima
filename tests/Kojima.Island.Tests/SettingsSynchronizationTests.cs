using Kojima.Core.Config;
using Kojima.Island.Settings;
using Xunit;

namespace Kojima.Island.Tests;

public sealed class SettingsSynchronizationTests : IslandTestBase
{
    [Fact]
    public void LoadsEveryEditableRefreshAndAnimationValue()
    {
        WithRuntime(config =>
        {
            config.Weather.RefreshMinutes = 45;
            config.Monitor.IntervalMs = 2400;
            config.Island.AnimationMs = 320;
        }, runtime =>
        {
            var window = new SettingsWindow(runtime);
            try
            {
                Assert.Equal("45", window.WeatherRefreshBox.Text);
                Assert.Equal("2400", window.MonitorIntervalBox.Text);
                Assert.Equal(320, window.AnimationSlider.Value);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void InvalidHotkeyDoesNotLockApplyButtons()
    {
        WithRuntime(null, runtime =>
        {
            var window = new SettingsWindow(runtime);
            try
            {
                window.HotkeyToggle.Text = "invalid";
                window.ApplyForTestAsync().GetAwaiter().GetResult();

                Assert.True(window.ApplyButton.IsEnabled);
                Assert.True(window.SaveButton.IsEnabled);
                Assert.True(window.CancelButton.IsEnabled);
                Assert.Contains("快捷键", window.StatusText.Text);
                Assert.Equal("Ctrl+Alt+K", runtime.Config.Hotkeys.Toggle);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void OutOfRangeNumberDoesNotMutateConfig()
    {
        WithRuntime(null, runtime =>
        {
            var window = new SettingsWindow(runtime);
            try
            {
                window.MessagePoll.Text = "100";
                window.ApplyForTestAsync().GetAwaiter().GetResult();

                Assert.Equal(1500, runtime.Config.Messages.PollMs);
                Assert.Contains("500", window.StatusText.Text);
                Assert.True(window.ApplyButton.IsEnabled);
                Assert.True(window.SaveButton.IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ReorderingDoesNotSaveUntilApply()
    {
        WithRuntime(config => config.Island.NavOrder = ["usage", "weather", "monitor"], runtime =>
        {
            var before = File.ReadAllText(ConfigStore.ConfigPath);
            var original = runtime.Config.Island.NavOrder.ToArray();
            var window = new SettingsWindow(runtime);
            try
            {
                window.DebugMove("weather", 1);

                Assert.Equal(original, runtime.Config.Island.NavOrder);
                Assert.Equal(before, File.ReadAllText(ConfigStore.ConfigPath));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ApplyPersistsAndReloadsAllEditedValues()
    {
        WithRuntime(config => config.Island.NavOrder = ["usage", "weather", "monitor"], runtime =>
        {
            var window = new SettingsWindow(runtime);
            try
            {
                window.WeatherRefreshBox.Text = "40";
                window.MonitorIntervalBox.Text = "2200";
                window.MessagePoll.Text = "2000";
                window.StockRefresh.Text = "20";
                window.RefreshBox.Text = "120";
                window.IdleExitBox.Text = "60";
                window.AnimationSlider.Value = 240;
                window.DebugMove("weather", 1);

                window.ApplyForTestAsync().GetAwaiter().GetResult();

                var saved = ConfigStore.Load();
                Assert.Equal(40, saved.Weather.RefreshMinutes);
                Assert.Equal(2200, saved.Monitor.IntervalMs);
                Assert.Equal(2000, saved.Messages.PollMs);
                Assert.Equal(20, saved.Stocks.RefreshSeconds);
                Assert.Equal(120, saved.Usage.RefreshSeconds);
                Assert.Equal(60, saved.Feature("monitor").IdleExitSeconds);
                Assert.Equal(240, saved.Island.AnimationMs);
                Assert.Equal("monitor", saved.Island.NavOrder[1]);
                Assert.Equal("weather", saved.Island.NavOrder[2]);
                Assert.Equal("已保存并立即生效", window.StatusText.Text);
            }
            finally
            {
                window.Close();
            }

            var reopened = new SettingsWindow(runtime);
            try
            {
                Assert.Equal("40", reopened.WeatherRefreshBox.Text);
                Assert.Equal("2200", reopened.MonitorIntervalBox.Text);
                Assert.Equal("2000", reopened.MessagePoll.Text);
                Assert.Equal(240, reopened.AnimationSlider.Value);
            }
            finally
            {
                reopened.Close();
            }
        });
    }

    [Fact]
    public void UsagePeriodIsNormalizedAndPersisted()
    {
        WithRuntime(config => config.Usage.Period = "week", runtime =>
        {
            Assert.Equal("week", runtime.Usage.Period);
            runtime.Usage.SetPeriod("MONTH");
            runtime.SaveConfig();

            Assert.Equal("month", runtime.Usage.Period);
            Assert.Equal("month", ConfigStore.Load().Usage.Period);
            runtime.Usage.SetPeriod("unknown");
            Assert.Equal("today", runtime.Config.Usage.Period);
        });
    }

    [Fact]
    public void WeatherReadsTheLiveRuntimeConfig()
    {
        WithRuntime(config =>
        {
            config.Weather.Location = " 上海 ";
            config.Weather.RefreshMinutes = 35;
        }, runtime =>
        {
            runtime.Weather.ApplySettings();

            Assert.Equal("上海", runtime.Weather.Location);
            Assert.Equal(35, runtime.Weather.RefreshMinutes);
        });
    }
}
