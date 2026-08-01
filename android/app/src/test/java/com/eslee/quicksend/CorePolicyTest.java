package com.eslee.quicksend;

import static org.junit.Assert.assertArrayEquals;
import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertThrows;
import static org.junit.Assert.assertTrue;

import com.eslee.quicksend.engine.CheckpointPolicy;
import com.eslee.quicksend.engine.MerkleAccumulator;
import com.eslee.quicksend.engine.ManualConnectFailure;
import com.eslee.quicksend.engine.ManualEndpoint;
import com.eslee.quicksend.engine.ManualEndpointValidationException;
import com.eslee.quicksend.engine.RetryPolicy;
import com.eslee.quicksend.protocol.FrameHeader;
import com.eslee.quicksend.protocol.MessageType;
import com.eslee.quicksend.protocol.ProtocolConstants;
import java.nio.charset.StandardCharsets;
import org.junit.Test;

public final class CorePolicyTest {
    @Test
    public void manualEndpointAcceptsLanIpv4AndPort() {
        var endpoint = ManualEndpoint.parse("192.168.123.102", "41231");
        assertEquals("192.168.123.102", endpoint.getAddress().getHostAddress());
        assertEquals(41231, endpoint.getPort());
    }

    @Test
    public void manualEndpointRejectsInvalidIp() {
        var error = assertThrows(
            ManualEndpointValidationException.class,
            () -> ManualEndpoint.parse("192.168.123.999", "41231")
        );
        assertEquals(ManualConnectFailure.INVALID_IP, error.getReason());
    }

    @Test
    public void manualEndpointRejectsInvalidPort() {
        var error = assertThrows(
            ManualEndpointValidationException.class,
            () -> ManualEndpoint.parse("192.168.123.102", "70000")
        );
        assertEquals(ManualConnectFailure.INVALID_PORT, error.getReason());
    }

    @Test
    public void dnsSdDiscoveryContractMatchesProtocolDocument() {
        assertEquals("_eslee-quicksend._tcp.", ProtocolConstants.SERVICE_TYPE);
        assertEquals(41231, ProtocolConstants.DEFAULT_PORT);
        assertEquals(1, ProtocolConstants.VERSION);
    }

    @Test
    public void frameHeaderRoundTrips64BitFields() {
        var original = new FrameHeader(MessageType.CHUNK_DATA, 4, 9_000_000_000L, 150L * 1024 * 1024 * 1024);
        assertEquals(original, FrameHeader.Companion.decode(original.encode()));
    }

    @Test
    public void merkleSnapshotResumesToSameRoot() {
        var first = new MerkleAccumulator();
        first.addChunk("first".getBytes(StandardCharsets.UTF_8));
        first.addChunk("second".getBytes(StandardCharsets.UTF_8));
        var resumed = MerkleAccumulator.Companion.restore(first.snapshot());
        first.addChunk("third".getBytes(StandardCharsets.UTF_8));
        resumed.addChunk("third".getBytes(StandardCharsets.UTF_8));
        assertArrayEquals(first.root(), resumed.root());
    }

    @Test
    public void retryScheduleCapsAtOneMinute() {
        var retry = new RetryPolicy();
        long[] expected = {2_000, 5_000, 10_000, 20_000, 30_000, 60_000, 60_000};
        for (var attempt = 0; attempt < expected.length; attempt++) {
            assertEquals(expected[attempt], retry.delayMillis(attempt));
        }
    }

    @Test
    public void checkpointUsesBytesOrElapsedTime() {
        var policy = new CheckpointPolicy(64, 1_000);
        policy.restore(0, 10_000);
        assertFalse(policy.due(63, 10_999));
        assertTrue(policy.due(64, 10_001));
        policy.committed(64, 10_001);
        assertTrue(policy.due(65, 11_001));
    }
}
