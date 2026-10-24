package org.jellyfin.androidtv.volumeguard;

import android.content.Context;
import androidx.media3.common.Format;
import androidx.media3.common.MimeTypes;
import androidx.media3.common.audio.AudioProcessor;
import androidx.media3.common.util.UnstableApi;
import androidx.media3.exoplayer.DefaultRenderersFactory;
import androidx.media3.exoplayer.audio.AudioSink;
import androidx.media3.exoplayer.audio.DefaultAudioSink;
import androidx.media3.exoplayer.audio.ForwardingAudioSink;

/** This companion always decodes audio, including Off, so toggles can take effect immediately. */
@UnstableApi
public final class VolumeGuardRenderersFactory extends DefaultRenderersFactory {
    private final VolumeGuardAudioProcessor processor;
    public VolumeGuardRenderersFactory(Context context, VolumeGuardAudioProcessor processor) {
        super(context); this.processor = processor;
    }
    @Override protected AudioSink buildAudioSink(Context context, boolean floatOutput, boolean platformSpeed) {
        AudioSink sink = new DefaultAudioSink.Builder(context)
            .setEnableFloatOutput(false)
            .setEnableAudioTrackPlaybackParams(false)
            .setAudioProcessors(new AudioProcessor[]{processor}).build();
        return new ForwardingAudioSink(sink) {
            @Override public boolean supportsFormat(Format format) {
                return MimeTypes.AUDIO_RAW.equals(format.sampleMimeType) && super.supportsFormat(format);
            }
            @Override public int getFormatSupport(Format format) {
                return MimeTypes.AUDIO_RAW.equals(format.sampleMimeType) ? super.getFormatSupport(format) : AudioSink.SINK_FORMAT_UNSUPPORTED;
            }
        };
    }
}
