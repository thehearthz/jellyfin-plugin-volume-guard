package org.jellyfin.androidtv.volumeguard;

import androidx.media3.common.C;
import androidx.media3.common.audio.BaseAudioProcessor;
import androidx.media3.common.audio.AudioProcessor.AudioFormat;
import androidx.media3.common.audio.AudioProcessor.UnhandledAudioFormatException;
import androidx.media3.common.util.UnstableApi;
import java.nio.ByteBuffer;

@UnstableApi
public final class VolumeGuardAudioProcessor extends BaseAudioProcessor {
    public final VolumeGuardDsp dsp = new VolumeGuardDsp();
    private float[] frame = new float[2];
    @Override protected AudioFormat onConfigure(AudioFormat format) throws UnhandledAudioFormatException {
        if (format.encoding != C.ENCODING_PCM_16BIT) throw new UnhandledAudioFormatException(format);
        frame = new float[format.channelCount];
        return format; // Always active: mode changes do not require rebuilding the player.
    }
    @Override public void queueInput(ByteBuffer input) {
        int bytes = input.remaining();
        int frameBytes = inputAudioFormat.channelCount * 2;
        if (bytes % frameBytes != 0) throw new IllegalArgumentException("Incomplete PCM frame");
        ByteBuffer output = replaceOutputBuffer(bytes);
        while (input.hasRemaining()) {
            for (int c = 0; c < frame.length; c++) frame[c] = input.getShort() / 32768f;
            dsp.processFrame(frame, frame.length);
            for (float value : frame) output.putShort((short) Math.max(-32768, Math.min(32767, Math.round(value * 32768))));
        }
        output.flip();
    }
    @Override protected void onFlush() { dsp.reset(inputAudioFormat.sampleRate); }
}
