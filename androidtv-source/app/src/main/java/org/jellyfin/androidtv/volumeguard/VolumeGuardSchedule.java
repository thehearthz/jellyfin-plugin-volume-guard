package org.jellyfin.androidtv.volumeguard;

import java.time.LocalTime;
import java.time.ZonedDateTime;

/** Membership is evaluated from an instant, in the schedule's zone. Sunday is zero. */
public final class VolumeGuardSchedule {
    private VolumeGuardSchedule() { }
    public static boolean isActive(ZonedDateTime now, LocalTime start, LocalTime end, int[] days) {
        int day = now.getDayOfWeek().getValue() % 7;
        LocalTime time = now.toLocalTime();
        if (start.equals(end)) return false;
        if (start.isBefore(end)) return contains(days, day) && !time.isBefore(start) && time.isBefore(end);
        return (contains(days, day) && !time.isBefore(start)) || (contains(days, (day + 6) % 7) && time.isBefore(end));
    }
    private static boolean contains(int[] days, int day) {
        for (int value : days) if (value == day) return true;
        return false;
    }
}
