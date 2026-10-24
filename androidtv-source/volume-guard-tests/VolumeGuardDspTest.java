import org.jellyfin.androidtv.volumeguard.VolumeGuardDsp;
import org.jellyfin.androidtv.volumeguard.VolumeGuardSchedule;
import java.time.*;

/** Dependency-free JVM checks. Run with assertions always on through this explicit check helper. */
public final class VolumeGuardDspTest {
    private static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
    }
    private static double amplitude(double db) { return Math.pow(10, db / 20); }
    private static void settle(VolumeGuardDsp dsp, int rate, int channels, float value, int seconds) {
        float[] frame = new float[channels];
        for (int i = 0; i < rate * seconds; i++) {
            for (int c = 0; c < channels; c++) frame[c] = value;
            dsp.processFrame(frame, channels);
        }
    }
    public static void main(String[] args) {
        for (int rate : new int[]{8000, 48000, 192000}) {
            VolumeGuardDsp dsp = new VolumeGuardDsp();
            dsp.reset(rate);
            dsp.setProfile(VolumeGuardDsp.OFF);
            float[] off = {.25f, -.75f};
            dsp.processFrame(off, 2);
            check(off[0] == .25f && off[1] == -.75f, "Off must bypass exactly");

            dsp.setProfile(VolumeGuardDsp.NIGHT);
            float[] coldPeak = {1, -1};
            dsp.processFrame(coldPeak, 2);
            check(Math.abs(coldPeak[0]) <= amplitude(-4) + .00001, "First frame must already obey ceiling");
            settle(dsp, rate, 6, .01f, 5);
            float[] quiet = {.01f, .005f, -.01f, 0, .003f, -.002f};
            dsp.processFrame(quiet, 6);
            check(quiet[0] > .012f, "Quiet audio should receive useful gain");
            check(quiet[0] <= .01 * amplitude(9) + .00001, "Gain must stay within configured maximum");
            check(Math.abs(quiet[0] / quiet[1] - 2) < .0001, "Linked gain must preserve channels");

            double ceiling = amplitude(-4);
            for (int i = 0; i < 500; i++) {
                float[] peak = {1, -.5f, .25f, 0, .9f, -.8f};
                dsp.processFrame(peak, 6);
                for (float v : peak) check(Math.abs(v) <= ceiling + .00001, "Sudden peak must obey sample ceiling");
                check(Math.abs(peak[0] / peak[1] + 2) < .0001, "Limiting must preserve channel ratios");
            }
            settle(dsp, rate, 2, 0, 4);
            float[] silence = {0, 0};
            dsp.processFrame(silence, 2);
            check(silence[0] == 0 && silence[1] == 0, "Silence must remain silent");
            float[] bad = {Float.NaN, Float.POSITIVE_INFINITY};
            dsp.processFrame(bad, 2);
            check(Float.isFinite(bad[0]) && Float.isFinite(bad[1]), "Nonfinite input must not poison DSP");

            dsp.setProfile(VolumeGuardDsp.OFF);
            settle(dsp, rate, 2, .1f, 1);
            float[] returned = {.1f, -.2f};
            dsp.processFrame(returned, 2);
            check(returned[0] == .1f && returned[1] == -.2f, "Ramp must settle to exact bypass");
        }
        VolumeGuardDsp.Profile invalid = new VolumeGuardDsp.Profile(true, Double.NaN, Double.POSITIVE_INFINITY, 20, -1, 0);
        check(Double.isFinite(invalid.targetDb) && invalid.maxGainDb <= 24 && invalid.ceilingDb <= 0, "Unsafe policy values must be bounded");
        LocalTime start = LocalTime.of(22, 0), end = LocalTime.of(7, 0);
        check(VolumeGuardSchedule.isActive(ZonedDateTime.parse("2026-10-20T06:59:59Z"), start, end, new int[]{1}), "Tuesday morning belongs to Monday window");
        check(!VolumeGuardSchedule.isActive(ZonedDateTime.parse("2026-10-20T07:00:00Z"), start, end, new int[]{1}), "End is exclusive");
        check(!VolumeGuardSchedule.isActive(ZonedDateTime.parse("2026-10-19T06:00:00Z"), start, end, new int[]{1}), "Monday morning does not belong to Monday night");
        check(VolumeGuardSchedule.isActive(ZonedDateTime.parse("2026-10-18T01:00:00Z"), start, end, new int[]{6}), "Saturday wraps to Sunday");
        check(!VolumeGuardSchedule.isActive(ZonedDateTime.parse("2026-10-18T01:00:00Z"), start, end, new int[]{}), "Empty schedule inactive");
        ZoneId eastern = ZoneId.of("America/New_York");
        for (String instant : new String[]{"2026-11-01T05:30:00Z", "2026-11-01T06:30:00Z"}) {
            check(VolumeGuardSchedule.isActive(Instant.parse(instant).atZone(eastern), LocalTime.of(1, 0), LocalTime.of(3, 0), new int[]{0}), "Both occurrences of repeated DST hour");
        }
        check(!VolumeGuardSchedule.isActive(Instant.parse("2026-03-08T07:00:00Z").atZone(eastern), LocalTime.of(1, 0), LocalTime.of(3, 0), new int[]{0}), "DST jump reaches exclusive end");
        check(!VolumeGuardSchedule.isActive(ZonedDateTime.parse("2026-10-19T12:00:00Z"), start, start, new int[]{1}), "Equal times are inactive");
        System.out.println("VolumeGuardDsp: all checks passed (8/48/192 kHz; stereo and six-channel PCM)");
        System.out.println("VolumeGuardSchedule: overnight, boundaries, week wrap and DST checks passed");
    }
}
