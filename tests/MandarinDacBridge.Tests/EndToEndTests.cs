        var sv = await Post("/api/services", "{\"qobuz\":true}");
        Assert.True(sv.GetProperty("qobuz").GetBoolean());
        Assert.True(sv.GetProperty("qobuzInstalled").GetBoolean());
        // QobuzProxy can take longer than the default 10 s to start on slower CI runners,
        // especially while the private Python env is created and the web API becomes responsive.
        await Until(async () => (await Dacs()).RootElement.GetProperty("services").GetProperty("qobuzStatus").GetString() == "ready · choose it in Qobuz · me@example.com", 20_000);

        var args = File.ReadAllLines(Path.Combine(data, "qobuz", "args"));
