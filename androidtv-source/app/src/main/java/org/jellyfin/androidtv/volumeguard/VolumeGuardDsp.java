package org.jellyfin.androidtv.volumeguard;

/** Streaming RMS leveling and linked sample-peak protection. Does not measure LUFS or true peaks. */
public final class VolumeGuardDsp {
    public static final class Profile {
        public final boolean enabled;
        public final double targetDb, maxGainDb, ceilingDb, attackMs, releaseMs;
        public Profile(boolean enabled, double targetDb, double maxGainDb, double ceilingDb, double attackMs, double releaseMs) {
            this.enabled = enabled;
            this.targetDb = finite(targetDb, -40, -6, -20);
            this.maxGainDb = finite(maxGainDb, 0, 24, 6);
            this.ceilingDb = finite(ceilingDb, -20, 0, -1);
            this.attackMs = finite(attackMs, 1, 200, 10);
            this.releaseMs = finite(releaseMs, 20, 5000, 500);
        }
        private static double finite(double x, double min, double max, double fallback) {
            return Double.isFinite(x) ? Math.max(min, Math.min(max, x)) : fallback;
        }
    }
    public static final Profile OFF = new Profile(false, -20, 0, -1, 50, 1000);
    public static final Profile BALANCED = new Profile(true, -20, 6, -1, 10, 500);
    public static final Profile NIGHT = new Profile(true, -26, 9, -4, 5, 800);
    private volatile Profile profile = OFF;
    private double energy, gainDb, peakGain = 1, wet;
    private int sampleRate = 48000;

    public void setProfile(Profile value) { profile = value == null ? OFF : value; }
    public void reset(int rate) {
        sampleRate = Math.max(8000, rate);
        energy = 0; gainDb = 0; peakGain = 1; wet = 0;
    }

    /** One interleaved frame; every channel receives the same gain to preserve spatial balance. */
    public void processFrame(float[] samples, int channels) {
        Profile p = profile;
        double power = 0, peak = 0;
        for (int c = 0; c < channels; c++) {
            if (!Float.isFinite(samples[c])) samples[c] = 0;
            double v = samples[c];
            power += v * v; peak = Math.max(peak, Math.abs(v));
        }
        power /= channels;
        double meter = Math.exp(-1.0 / (sampleRate * 0.4));
        energy = meter * energy + (1 - meter) * power;
        // Silence gate: do not chase noise during pauses. Limiting remains active.
        if (power > 0.0000031623 && energy > 1e-10) {
            double wanted = Math.max(-24, Math.min(p.maxGainDb, p.targetDb - 10 * Math.log10(energy)));
            double smoothing = Math.exp(-1.0 / (sampleRate * (wanted < gainDb ? p.attackMs : p.releaseMs) / 1000));
            gainDb = smoothing * gainDb + (1 - smoothing) * wanted;
        }
        double levelGain = Math.pow(10, gainDb / 20);
        double ceiling = Math.pow(10, p.ceilingDb / 20);
        double needed = peak == 0 ? 1 : Math.min(1, ceiling / (peak * levelGain));
        peakGain = needed < peakGain ? needed : needed + (peakGain - needed) * Math.exp(-1.0 / (sampleRate * .08));
        wet += ((p.enabled ? 1 : 0) - wet) * (1 - Math.exp(-1.0 / (sampleRate * .02)));
        if (!p.enabled && wet < .00001) wet = 0;
        double applied = 1 + wet * (levelGain * peakGain - 1);
        // Final linked sample cap also covers profile changes and the bypass crossfade.
        double rampCeiling = p.enabled ? ceiling : 1 + wet * (ceiling - 1);
        if (peak * applied > rampCeiling) applied = rampCeiling / peak;
        for (int c = 0; c < channels; c++) samples[c] *= (float) applied;
    }
}
