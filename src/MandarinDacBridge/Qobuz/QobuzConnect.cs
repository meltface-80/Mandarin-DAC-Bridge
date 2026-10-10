    private void RunProxy(int count)
    {
        var psi = new ProcessStartInfo(VenvPython) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-m", "qobuz_proxy", "--config", Path.Combine(dir, "config.yaml") }) psi.ArgumentList.Add(a);
        psi.Environment["QOBUZPROXY_DATA_DIR"] = dir;
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        var lines = new Queue<string>();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("qobuz-proxy didn't start");
        void Line(string? l)
        {
            if (l == null) return;
            lock (lines) { lines.Enqueue(l); while (lines.Count > 30) lines.Dequeue(); }
            if (l.Contains("ERROR") || l.Contains("Traceback") || l.Contains("Exception")) Log(l.Length > 200 ? l[..200] : l);
        }
        p.OutputDataReceived += (_, e) => Line(e.Data);
        p.ErrorDataReceived += (_, e) => Line(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        lock (gate) proc = p;
        Log($"offering {count} DAC{(count == 1 ? "" : "s")} to Qobuz (web page on port {WebPort})");
        SetStatus("starting…");
        lastError = "";
        var start = DateTime.UtcNow;
        try
        {
            while (!p.HasExited && !stop.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny([changed, stop.Token.WaitHandle], 3000) == 0) { Log("the DACs changed: starting again"); return; }
                PollStatus();
                if ((DateTime.UtcNow - start).TotalSeconds > 20)
                {
                    string last;
                    lock (lines) last = lines.LastOrDefault(l => l.Trim() != "") ?? "";
                    throw new InvalidOperationException($"qobuz-proxy did not become ready after 20s{(last != "" ? ": " + last[..Math.Min(200, last.Length)] : "")}");
                }
            }
            if (stop.IsCancellationRequested) return;
            string last;
            lock (lines) last = lines.LastOrDefault(l => l.Trim() != "") ?? "";
            throw new InvalidOperationException($"qobuz-proxy stopped ({p.ExitCode}){(last != "" ? ": " + last[..Math.Min(200, last.Length)] : "")}");
        }
        finally
        {
            lock (gate) proc = null;
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); p.WaitForExit(3000); } catch (Exception) { /* gone */ }
            SignedIn = false;
        }
    }
