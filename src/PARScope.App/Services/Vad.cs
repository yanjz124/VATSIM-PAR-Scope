namespace PARScope.App.Services;

/// <summary>
/// Simple energy-based voice activity detection on 16 kHz audio, used to split a transmission at the
/// controller's natural pauses ("turn left heading two four zero … slightly below glidepath …").
/// </summary>
public static class Vad
{
    private const int Frame = 320;          // 20 ms
    private const int MinSpeechFrames = 75; // 1.5 s of speech before a pause can end a phrase
    private const int PauseFrames = 18;     // 0.36 s of quiet

    /// <summary>Sample index in the middle of the first pause after <paramref name="from"/> that ends a
    /// phrase, or -1 if there is none yet.</summary>
    public static int FindPause(float[] s, int from)
    {
        var rms = FrameRms(s, from);
        if (rms.Length < MinSpeechFrames + PauseFrames) return -1;
        float threshold = Threshold(rms);
        int speech = 0, quiet = 0;
        for (int i = 0; i < rms.Length; i++)
        {
            if (rms[i] > threshold) { speech++; quiet = 0; }
            else if (speech >= MinSpeechFrames && ++quiet >= PauseFrames)
                return from + (i - PauseFrames / 2) * Frame;
        }
        return -1;
    }

    /// <summary>Whether a piece has enough speech to be worth transcribing (Whisper invents words for silence).</summary>
    public static bool HasSpeech(float[] s)
    {
        var rms = FrameRms(s, 0);
        if (rms.Length == 0) return false;
        float threshold = Threshold(rms);
        return rms.Count(r => r > threshold) >= 10;
    }

    private static float[] FrameRms(float[] s, int from)
    {
        int n = Math.Max(0, (s.Length - from) / Frame);
        var rms = new float[n];
        for (int f = 0; f < n; f++)
        {
            double sum = 0;
            int o = from + f * Frame;
            for (int i = 0; i < Frame; i++) sum += s[o + i] * s[o + i];
            rms[f] = (float)Math.Sqrt(sum / Frame);
        }
        return rms;
    }

    /// <summary>Speech is well above the quietest frames (the room/mic noise floor).</summary>
    private static float Threshold(float[] rms)
    {
        var sorted = rms.OrderBy(r => r).ToArray();
        float floor = sorted[(int)(sorted.Length * 0.15)];
        return Math.Max(0.008f, floor * 3);
    }
}
