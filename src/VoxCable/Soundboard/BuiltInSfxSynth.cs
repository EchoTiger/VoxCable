namespace VoxCable.Soundboard;

public static class BuiltInSfxSynth
{
    public static readonly string[] DefaultPadNames =
    {
        // Bank 1 (Built-in Procedural Synths)
        "Airhorn",
        "Laser Pew",
        "Rimshot",
        "Censor Beep",
        "Sad Trombone",
        "Bell Chime",
        "Sci-Fi Alarm",
        "Victory Fanfare",
        // Bank 2 (Custom Drag & Drop Slots)
        "+ Custom Slot 9",
        "+ Custom Slot 10",
        "+ Custom Slot 11",
        "+ Custom Slot 12",
        "+ Custom Slot 13",
        "+ Custom Slot 14",
        "+ Custom Slot 15",
        "+ Custom Slot 16"
    };

    public static float[]? Generate(int padIndex, int sampleRate)
    {
        sampleRate = Math.Max(8000, sampleRate);
        return padIndex switch
        {
            0 => GenerateAirhorn(sampleRate),
            1 => GenerateLaserPew(sampleRate),
            2 => GenerateRimshot(sampleRate),
            3 => GenerateCensorBeep(sampleRate),
            4 => GenerateSadTrombone(sampleRate),
            5 => GenerateBellChime(sampleRate),
            6 => GenerateSciFiAlarm(sampleRate),
            7 => GenerateVictoryFanfare(sampleRate),
            _ => null
        };
    }

    private static float[] GenerateAirhorn(int sr)
    {
        int frames = (int)(sr * 0.95);
        var buf = new float[frames * 2];
        double p1 = 0, p2 = 0, p3 = 0, p4 = 0;

        for (int i = 0; i < frames; i++)
        {
            double t = i / (double)sr;
            double pitchSlide = t < 0.04 ? 0.93 + 0.07 * (t / 0.04) : 1.0;

            p1 += (440.0 * pitchSlide) / sr;
            p2 += (443.5 * pitchSlide) / sr;
            p3 += (554.37 * pitchSlide) / sr;
            p4 += (659.25 * pitchSlide) / sr;

            double raw = Saw(p1) + Saw(p2) * 0.85 + Saw(p3) * 0.75 + Saw(p4) * 0.65;
            double env = Envelope(t, 0.012, 0.118, frames / (double)sr);
            float sample = MathF.Tanh((float)(raw * 0.25 * env)) * 0.65f;

            buf[i * 2] = sample;
            buf[i * 2 + 1] = sample;
        }
        return buf;
    }

    private static float[] GenerateLaserPew(int sr)
    {
        int frames = (int)(sr * 0.45);
        var buf = new float[frames * 2];
        double phase = 0;
        double modPhase = 0;

        for (int i = 0; i < frames; i++)
        {
            double t = i / (double)sr;
            double freq = 140.0 + 1660.0 * Math.Exp(-t * 11.5);
            phase += 2.0 * Math.PI * freq / sr;
            modPhase += 2.0 * Math.PI * 38.0 / sr;

            double mod = 0.45 * Math.Sin(modPhase);
            double env = Envelope(t, 0.004, 0.146, frames / (double)sr);
            float sample = (float)(Math.Sin(phase + mod) * env * 0.55);

            buf[i * 2] = sample;
            buf[i * 2 + 1] = sample;
        }
        return buf;
    }

    private static float[] GenerateRimshot(int sr)
    {
        int frames = (int)(sr * 1.15);
        var buf = new float[frames * 2];
        uint rng = 0x9E3779B9u;
        double tomPhase = 0;
        double snarePhase = 0;

        for (int i = 0; i < frames; i++)
        {
            double t = i / (double)sr;
            double sample = 0;

            // Hit 1: Tom @ t = 0.00 (proper integrated phase accumulator)
            if (t < 0.22)
            {
                double f = 180.0 * Math.Exp(-t * 14.0) + 72.0;
                tomPhase += 2.0 * Math.PI * f / sr;
                sample += Math.Sin(tomPhase) * Math.Exp(-t * 18.0) * 0.75;
            }

            // Hit 2: Snare @ t = 0.22 (proper integrated phase accumulator)
            if (t >= 0.22 && t < 0.46)
            {
                double dt = t - 0.22;
                double f = Math.Max(90.0, 210.0 - dt * 180.0);
                snarePhase += 2.0 * Math.PI * f / sr;
                double body = Math.Sin(snarePhase) * Math.Exp(-dt * 24.0);
                double noise = NextNoise(ref rng) * Math.Exp(-dt * 18.0);
                sample += (body * 0.5 + noise * 0.65) * 0.80;
            }

            // Hit 3: Crash cymbal @ t = 0.46
            if (t >= 0.46)
            {
                double dt = t - 0.46;
                double shimmer = Math.Sin(2.0 * Math.PI * 3420.0 * dt) * 0.15 + Math.Sin(2.0 * Math.PI * 5180.0 * dt) * 0.10;
                double crash = (NextNoise(ref rng) * 0.85 + shimmer) * Math.Exp(-dt * 4.5);
                sample += crash * 0.55;
            }

            float outS = Math.Clamp((float)sample, -0.85f, 0.85f);
            buf[i * 2] = outS;
            buf[i * 2 + 1] = outS;
        }
        return buf;
    }

    private static float[] GenerateCensorBeep(int sr)
    {
        int frames = (int)(sr * 0.55);
        var buf = new float[frames * 2];
        double total = frames / (double)sr;

        for (int i = 0; i < frames; i++)
        {
            double t = i / (double)sr;
            double env = Envelope(t, 0.008, 0.008, total);
            float s = (float)(Math.Sin(2.0 * Math.PI * 1000.0 * t) * env * 0.42);
            buf[i * 2] = s;
            buf[i * 2 + 1] = s;
        }
        return buf;
    }

    private static float[] GenerateSadTrombone(int sr)
    {
        int frames = (int)(sr * 1.95);
        var buf = new float[frames * 2];

        double[] noteStarts = { 0.00, 0.42, 0.84, 1.26 };
        double[] noteEnds = { 0.39, 0.81, 1.23, 1.92 };
        double[] freqs = { 293.66, 277.18, 261.63, 246.94 };

        double phase = 0;
        for (int i = 0; i < frames; i++)
        {
            double t = i / (double)sr;
            double sample = 0;

            for (int n = 0; n < 4; n++)
            {
                if (t >= noteStarts[n] && t < noteEnds[n])
                {
                    double dt = t - noteStarts[n];
                    double dur = noteEnds[n] - noteStarts[n];
                    double f = freqs[n];

                    if (n == 3)
                    {
                        f -= dt * 32.0;
                        f += Math.Sin(2.0 * Math.PI * 6.5 * dt) * 4.5;
                    }

                    phase += f / sr;
                    double brass = Saw(phase) * 0.55 + Math.Sin(2.0 * Math.PI * phase) * 0.45 + Saw(phase * 2.0) * 0.15;
                    double env = Envelope(dt, 0.025, 0.05, dur);
                    sample = MathF.Tanh((float)(brass * env * 0.6)) * 0.55;
                    break;
                }
            }

            buf[i * 2] = (float)sample;
            buf[i * 2 + 1] = (float)sample;
        }

        return buf;
    }

    private static float[] GenerateBellChime(int sr)
    {
        int frames = (int)(sr * 1.25);
        var buf = new float[frames * 2];
        double[] freqs = { 1046.50, 1318.51, 1567.98, 2093.00 };

        for (int i = 0; i < frames; i++)
        {
            double t = i / (double)sr;
            double sum = 0;
            for (int k = 0; k < freqs.Length; k++)
            {
                double f = freqs[k];
                double decay = Math.Exp(-t * (2.6 + k * 0.7));
                double attack = Math.Min(1.0, t / 0.005);
                sum += Math.Sin(2.0 * Math.PI * f * t) * decay * attack * (0.35 / (k * 0.4 + 1.0));
            }
            float s = (float)Math.Clamp(sum, -0.8, 0.8);
            buf[i * 2] = s;
            buf[i * 2 + 1] = s;
        }
        return buf;
    }

    private static float[] GenerateSciFiAlarm(int sr)
    {
        int frames = (int)(sr * 1.10);
        var buf = new float[frames * 2];
        double phase = 0;
        double total = frames / (double)sr;

        for (int i = 0; i < frames; i++)
        {
            double t = i / (double)sr;
            double cycle = (t % 0.36) / 0.36;
            double freq = 520.0 + 420.0 * Math.Sin(Math.PI * cycle);
            phase += 2.0 * Math.PI * freq / sr;

            double raw = Math.Sin(phase) * 0.65 + Math.Sin(phase * 2.0) * 0.25 + Saw(phase / (2.0 * Math.PI)) * 0.15;
            double env = Envelope(t, 0.015, 0.08, total);
            float s = (float)Math.Clamp(raw * env * 0.48, -0.8, 0.8);
            buf[i * 2] = s;
            buf[i * 2 + 1] = s;
        }
        return buf;
    }

    private static float[] GenerateVictoryFanfare(int sr)
    {
        int frames = (int)(sr * 1.35);
        var buf = new float[frames * 2];
        double[] starts = { 0.00, 0.15, 0.30, 0.48 };
        double[] ends = { 0.14, 0.29, 0.46, 1.32 };
        double[] freqs = { 523.25, 659.25, 783.99, 1046.50 }; // C5 E5 G5 C6

        double phase = 0;
        for (int i = 0; i < frames; i++)
        {
            double t = i / (double)sr;
            double sample = 0;
            for (int n = 0; n < starts.Length; n++)
            {
                if (t >= starts[n] && t < ends[n])
                {
                    double dt = t - starts[n];
                    double dur = ends[n] - starts[n];
                    double f = freqs[n];
                    phase += f / sr;
                    double env = Envelope(dt, 0.01, 0.12, dur);
                    double tone = Math.Sin(2.0 * Math.PI * phase) * 0.55
                                + Saw(phase) * 0.28
                                + Math.Sin(4.0 * Math.PI * phase) * 0.20;
                    sample = MathF.Tanh((float)(tone * env * 0.7)) * 0.52;
                    break;
                }
            }
            buf[i * 2] = (float)sample;
            buf[i * 2 + 1] = (float)sample;
        }
        return buf;
    }

    private static double Saw(double phase)
    {
        double frac = phase - Math.Floor(phase);
        return 2.0 * frac - 1.0;
    }

    private static double Envelope(double t, double attack, double release, double total)
    {
        if (t < attack && attack > 0) return t / attack;
        double relStart = total - release;
        if (t > relStart && release > 0) return Math.Max(0.0, 1.0 - (t - relStart) / release);
        return t <= total ? 1.0 : 0.0;
    }

    private static double NextNoise(ref uint rng)
    {
        rng ^= rng << 13;
        rng ^= rng >> 17;
        rng ^= rng << 5;
        return ((rng & 0xFFFF) / 32768.0) - 1.0;
    }
}
