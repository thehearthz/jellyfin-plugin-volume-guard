package org.jellyfin.androidtv.ui.playback.overlay.action

import android.content.Context
import android.view.Gravity
import android.view.View
import android.widget.PopupMenu
import org.jellyfin.androidtv.R
import org.jellyfin.androidtv.ui.playback.PlaybackController
import org.jellyfin.androidtv.ui.playback.overlay.CustomPlaybackTransportControlGlue
import org.jellyfin.androidtv.ui.playback.overlay.VideoPlayerAdapter
import org.jellyfin.androidtv.util.popupMenu

class VolumeGuardAction(context: Context, glue: CustomPlaybackTransportControlGlue) : CustomAction(context, glue) {
	private var popup: PopupMenu? = null
	init { initializeWithIcon(R.drawable.ic_volume_guard) }
	override fun handleClickAction(playbackController: PlaybackController, videoPlayerAdapter: VideoPlayerAdapter, context: Context, view: View) {
		val controller = playbackController.volumeGuard ?: return
		videoPlayerAdapter.leanbackOverlayFragment.setFading(false)
		dismissPopup()
		popup = popupMenu(context, view, Gravity.END) {
			for (mode in listOf("Off", "Balanced", "Night", "Auto")) {
				item(mode) { controller.selectMode(mode) }.apply { isChecked = controller.getMode() == mode }
			}
			item("Settings…") { controller.showSettings() }
		}
		popup?.menu?.setGroupCheckable(0, true, true)
		popup?.setOnDismissListener { videoPlayerAdapter.leanbackOverlayFragment.setFading(true); popup = null }
		popup?.show()
	}
	fun dismissPopup() { popup?.dismiss() }
}
