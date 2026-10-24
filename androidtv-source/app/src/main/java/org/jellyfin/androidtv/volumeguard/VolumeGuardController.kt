package org.jellyfin.androidtv.volumeguard

import android.app.AlertDialog
import android.app.TimePickerDialog
import android.content.Context
import android.os.Handler
import android.os.Looper
import androidx.media3.common.util.UnstableApi
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.jellyfin.androidtv.auth.repository.ServerRepository
import org.jellyfin.androidtv.auth.repository.SessionRepository
import org.json.JSONObject
import org.koin.java.KoinJavaComponent
import java.net.HttpURLConnection
import java.net.URL
import java.time.LocalTime
import java.time.ZoneId
import java.time.ZonedDateTime

/** Per-device/server/user selection. Network failures preserve cached policy and local controls. */
@UnstableApi
class VolumeGuardController(private val context: Context, private val processor: VolumeGuardAudioProcessor) {
	private val session = KoinJavaComponent.get<SessionRepository>(SessionRepository::class.java).currentSession.value
	private val server = KoinJavaComponent.get<ServerRepository>(ServerRepository::class.java).currentServer.value
	private val preferences = context.getSharedPreferences("volume_guard", Context.MODE_PRIVATE)
	private val key = "${session?.serverId}:${session?.userId}:"
	private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
	private val handler = Handler(Looper.getMainLooper())
	private var closed = false
	private var fetching = false
	private var lastFetch = 0L
	private var policy = runCatching { JSONObject(preferences.getString(key + "policy", "{}")!!) }.getOrDefault(JSONObject())
	private var mode = preferences.getString(key + "mode", "Auto") ?: "Auto"
	private var status = "Local defaults; server policy not yet refreshed"
	private var effectiveMode = "Off"
	private val tick = object : Runnable {
		override fun run() {
			if (closed) return
			apply()
			if (System.currentTimeMillis() - lastFetch >= 60_000 && !fetching) refresh()
			handler.postDelayed(this, 15_000)
		}
	}

	init {
		if (!preferences.contains(key + "mode")) mode = canonical(policy.optString("defaultMode", "Auto"))
		apply(); handler.post(tick)
	}

	fun getMode(): String = mode
	fun selectMode(value: String) {
		if (value !in listOf("Off", "Balanced", "Night", "Auto")) return
		mode = value
		preferences.edit().putString(key + "mode", value).apply()
		apply()
	}

	private fun refresh() {
		val address = server?.address ?: return
		val token = session?.accessToken ?: return
		fetching = true
		lastFetch = System.currentTimeMillis()
		scope.launch {
			val result = runCatching {
				withContext(Dispatchers.IO) {
					val connection = URL(address.trimEnd('/') + "/VolumeGuard/policy").openConnection() as HttpURLConnection
					try {
						connection.instanceFollowRedirects = false
						connection.connectTimeout = 5000
						connection.readTimeout = 5000
						connection.setRequestProperty("X-Emby-Token", token)
						check(connection.responseCode == 200) { "Policy unavailable" }
						val text = connection.inputStream.bufferedReader().use { reader ->
							val result = StringBuilder()
							val buffer = CharArray(4096)
							while (true) {
								val count = reader.read(buffer)
								if (count < 0) break
								check(result.length + count <= 131072) { "Policy too large" }
								result.append(buffer, 0, count)
							}
							result.toString()
						}
						JSONObject(text).also { check(it.optInt("schemaVersion") == 1) { "Unsupported policy" } }
					} finally { connection.disconnect() }
				}
			}
			if (!closed) {
				result.onSuccess {
					policy = it
					preferences.edit().putString(key + "policy", it.toString()).apply()
					if (!preferences.contains(key + "mode")) mode = canonical(it.optString("defaultMode", "Auto"))
					status = "Server policy connected"
				}.onFailure { status = "Server policy unavailable; using saved policy or local defaults" }
				apply()
			}
			fetching = false
		}
	}

	private fun canonical(value: String): String = listOf("Off", "Balanced", "Night", "Auto").firstOrNull { it.equals(value, true) } ?: "Auto"
	private fun configuration() = policy.optJSONObject("configuration") ?: JSONObject()

	private fun autoMode(): String {
		val config = configuration()
		val localSchedule = preferences.getBoolean(key + "localSchedule", false)
		val zone = if (localSchedule || !config.has("timeZoneId")) ZoneId.systemDefault() else {
			runCatching { ZoneId.of(config.getString("timeZoneId")) }.getOrElse {
				// Windows time-zone identifiers may be valid on the server but unavailable on Android.
				// Use the server's evaluated mode rather than silently switching to the TV's zone.
				return canonical(policy.optString("effectiveAutoMode", "Off"))
			}
		}
		val now = ZonedDateTime.now(zone)
		val time = now.toLocalTime()
		if (localSchedule) {
			val start = runCatching { LocalTime.parse(preferences.getString(key + "start", "22:00")) }.getOrDefault(LocalTime.of(22, 0))
			val end = runCatching { LocalTime.parse(preferences.getString(key + "end", "07:00")) }.getOrDefault(LocalTime.of(7, 0))
			val active = VolumeGuardSchedule.isActive(now, start, end, intArrayOf(0, 1, 2, 3, 4, 5, 6))
			return if (active) "Night" else "Balanced"
		}
		if (!config.optBoolean("scheduleEnabled", true)) return canonical(config.optString("daytimeMode", "Balanced"))
		val windows = config.optJSONArray("nightWindows")
		if (windows == null) return if (time >= LocalTime.of(22, 0) || time < LocalTime.of(7, 0)) "Night" else "Balanced"
		for (i in 0 until windows.length()) {
			val window = windows.optJSONObject(i) ?: continue
			val active = runCatching {
				val start = LocalTime.parse(window.getString("start"))
				val end = LocalTime.parse(window.getString("end"))
				val days = window.getJSONArray("days")
				VolumeGuardSchedule.isActive(now, start, end, IntArray(days.length()) { days.optInt(it, -1) })
			}.getOrDefault(false)
			if (active) return "Night"
		}
		return canonical(config.optString("daytimeMode", "Balanced"))
	}

	private fun apply() {
		val config = configuration()
		val effective = if (!policy.optBoolean("enabled", true) || !config.optBoolean("enabled", true)) "Off" else if (mode == "Auto") autoMode() else mode
		effectiveMode = effective
		val fallback = if (effective == "Night") VolumeGuardDsp.NIGHT else VolumeGuardDsp.BALANCED
		val settings = config.optJSONObject(effective.lowercase()) ?: JSONObject()
		processor.dsp.setProfile(if (effective == "Off") VolumeGuardDsp.OFF else VolumeGuardDsp.Profile(true,
			settings.optDouble("targetRmsDb", fallback.targetDb), settings.optDouble("maxGainDb", fallback.maxGainDb),
			settings.optDouble("limiterCeilingDb", fallback.ceilingDb), settings.optDouble("attackMs", fallback.attackMs), settings.optDouble("releaseMs", fallback.releaseMs)))
	}

	fun showSettings() {
		val local = preferences.getBoolean(key + "localSchedule", false)
		val start = preferences.getString(key + "start", "22:00")!!
		val end = preferences.getString(key + "end", "07:00")!!
		AlertDialog.Builder(context).setTitle("Volume Guard settings")
			.setItems(arrayOf("$mode → $effectiveMode. $status", "Auto schedule: ${if (local) "This TV" else "Server policy"}", "Night begins: $start", "Night ends: $end", "About audio processing")) { _, index ->
				when (index) {
					1 -> { preferences.edit().putBoolean(key + "localSchedule", !local).apply(); apply(); showSettings() }
					2, 3 -> {
						val value = LocalTime.parse(if (index == 2) start else end)
						TimePickerDialog(context, { _, hour, minute ->
							preferences.edit().putString(key + if (index == 2) "start" else "end", "%02d:%02d".format(java.util.Locale.ROOT, hour, minute)).putBoolean(key + "localSchedule", true).apply()
							apply()
						}, value.hour, value.minute, true).show()
					}
					4 -> AlertDialog.Builder(context).setTitle("Volume Guard")
						.setMessage("Levels quiet and loud sections using gated RMS gain and a linked sample-peak limiter. This is not LUFS or true-peak normalization. This companion decodes audio to PCM even in Off mode, allowing instant changes. Audio passthrough and Atmos metadata are unavailable. Auto checks the schedule every 15 seconds. Playback on external players is not processed.")
						.setPositiveButton("OK", null).show()
				}
			}.setNegativeButton("Close", null).show()
	}

	fun close() { closed = true; handler.removeCallbacks(tick); scope.cancel() }
}
