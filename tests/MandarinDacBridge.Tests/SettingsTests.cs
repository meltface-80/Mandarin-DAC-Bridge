// SettingsTests.cs — settings.json and roon-api.json hold secrets and every DAC's settings: they are
// their owner's only, written whole, and a file that can't be read is kept, never overwritten.
using Xunit;

namespace MandarinDacBridge.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("settings-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private string FileIn(string name) => Path.Combine(dir, name);

    private string[] Aside(string name) => Directory.GetFiles(dir, name + ".unreadable-*");

    private static void AssertOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows()) return;     // there the folder's permissions decide
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void NoFileYet_IsTheDefaults_AndNothingIsWrittenOrKeptAside()
    {
        var s = new Settings(dir);
        Assert.True(s.For("dac-1").Enabled);
        Assert.Null(s.Services().Spotify);
        Assert.False(File.Exists(FileIn("settings.json")));
        Assert.Empty(Aside("settings.json"));
    }

    [Fact]
    public void AFileThatCantBeRead_IsKeptAside_NotOverwrittenByTheDefaults()
    {
        const string cut = "{\"dacs\":{\"dac-1\":{\"name\":\"Study\"}},\"services\":{\"soloistKey\":\"sec";
        File.WriteAllText(FileIn("settings.json"), cut);

        var s = new Settings(dir);
        Assert.Equal("", s.For("dac-1").Name);                  // started from the defaults
        var aside = Assert.Single(Aside("settings.json"));
        Assert.Equal(cut, File.ReadAllText(aside));             // what was there, untouched

        s.Apply("dac-1", new SettingsPatch { Name = "Lounge" });
        Assert.Equal("Lounge", new Settings(dir).For("dac-1").Name);
        Assert.Equal(cut, File.ReadAllText(Assert.Single(Aside("settings.json"))));
    }

    [Fact]
    public void AFileThatCantBeOpened_IsLeftAlone_AndNeverWrittenOver()
    {
        // Held open by something else (a backup, another process): it can't be read, and it can't be
        // told from a broken one, so it is neither moved nor written over (a rename would replace it).
        const string kept = "{\"dacs\":{\"dac-1\":{\"name\":\"Study\"}}}";
        File.WriteAllText(FileIn("settings.json"), kept);
        Settings s;
        using (new FileStream(FileIn("settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            s = new Settings(dir);
            Assert.Equal("", s.For("dac-1").Name);
            s.Apply("dac-1", new SettingsPatch { Name = "Lounge" });
        }
        Assert.Equal(kept, File.ReadAllText(FileIn("settings.json")));
        Assert.Empty(Aside("settings.json"));
    }

    [Fact]
    public void JsonThatIsntSettings_StartsTheDefaults_WithoutFailing()
    {
        File.WriteAllText(FileIn("settings.json"), "{\"dacs\":null,\"services\":null}");
        var s = new Settings(dir);
        Assert.True(s.For("dac-1").Enabled);
        Assert.Null(s.Services().Spotify);
        s.Apply("dac-1", new SettingsPatch { Name = "Lounge" });
        Assert.Equal("Lounge", new Settings(dir).For("dac-1").Name);
    }

    [Fact]
    public void ALevelAlone_IsNotKept_SoNothingIsWritten()
    {
        var s = new Settings(dir);
        s.Apply("dac-1", new SettingsPatch { Name = "Lounge" });
        File.Delete(FileIn("settings.json"));
        s.Apply("dac-1", new SettingsPatch { Level = 40 });     // the volume slider, many times a second
        Assert.False(File.Exists(FileIn("settings.json")));
        s.Apply("dac-1", new SettingsPatch { Share = true });
        Assert.True(File.Exists(FileIn("settings.json")));
    }

    [Theory]
    [InlineData("E (plex) GET http://10.0.0.5:32400/library?X-Plex-Token=abc123&x=1 failed", "abc123")]
    [InlineData("set failed: plex.token=owner-token-9 rejected", "owner-token-9")]
    public void WhatCalderaPrints_NeverCarriesAPlexToken(string line, string secret)
    {
        var clean = Caldera.CalderaPlayer.Clean(line, "owner-token-9");
        Assert.DoesNotContain(secret, clean);
    }

    [Fact]
    public void Saved_OwnerOnly_AndWhole_EvenOverAnOlderFileOthersCouldRead()
    {
        File.WriteAllText(FileIn("settings.json"), "{}");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(FileIn("settings.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var s = new Settings(dir);
        s.Apply(new ServiceSettings { SoloistKey = "secret-key", PlexToken = "plex-token" });

        AssertOwnerOnly(FileIn("settings.json"));
        Assert.Empty(Directory.GetFiles(dir, "*.new"));
        Assert.Equal(("secret-key", "plex-token"), (new Settings(dir).Services().SoloistKey, new Settings(dir).Services().PlexToken));
    }

    [Fact]
    public void RoonTokens_AreKeptForEveryCore_OwnerOnly_AndAnUnreadableFileIsKeptAside()
    {
        var file = FileIn("roon-api.json");
        Assert.Empty(OwnerOnlyFile.ReadStrings(file));

        OwnerOnlyFile.WriteStrings(file, new Dictionary<string, string> { ["core-1"] = "tok-1" });
        var all = OwnerOnlyFile.ReadStrings(file);
        all["core-2"] = "tok-2";
        OwnerOnlyFile.WriteStrings(file, all);
        Assert.Equal(new Dictionary<string, string> { ["core-1"] = "tok-1", ["core-2"] = "tok-2" }, OwnerOnlyFile.ReadStrings(file));
        AssertOwnerOnly(file);

        File.WriteAllText(file, "[\"not\", \"an object\"]");
        Assert.Empty(OwnerOnlyFile.ReadStrings(file));
        Assert.Equal("[\"not\", \"an object\"]", File.ReadAllText(Assert.Single(Aside("roon-api.json"))));
        Assert.False(File.Exists(file));
    }
}
