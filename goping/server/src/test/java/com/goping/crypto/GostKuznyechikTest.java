package com.goping.crypto;

import org.junit.jupiter.api.Test;

import java.util.HexFormat;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

/**
 * Векторы из ГОСТ Р 34.13-2015 (приложение А.2) для «Кузнечика» в CTR.
 */
class GostKuznyechikTest {

    private static final HexFormat HEX = HexFormat.of();
    private static final byte[] KEY = HEX.parseHex(
            "8899aabbccddeeff0011223344556677fedcba98765432100123456789abcdef");

    @Test
    void ctrMatchesGost3413TestVector() {
        byte[] nonce = HEX.parseHex("1234567890abcef0");
        byte[] plaintext = HEX.parseHex(
                "1122334455667700ffeeddccbbaa9988"
                        + "00112233445566778899aabbcceeff0a"
                        + "112233445566778899aabbcceeff0a00"
                        + "2233445566778899aabbcceeff0a0011");
        byte[] expected = HEX.parseHex(
                "f195d8bec10ed1dbd57b5fa240bda1b8"
                        + "85eee733f6a13e5df33ce4b33c45dee4"
                        + "a5eae88be6356ed3d5e877f13564a3a5"
                        + "cb91fab1f20cbab6d1c6d15820bdba73");

        byte[] ciphertext = GostKuznyechik.ctr(KEY, nonce, plaintext);

        assertThat(ciphertext).isEqualTo(expected);
        assertThat(GostKuznyechik.ctr(KEY, nonce, ciphertext)).isEqualTo(plaintext);
    }

    @Test
    void ctrHandlesPartialLastBlock() {
        byte[] nonce = HEX.parseHex("0001020304050607");
        byte[] plaintext = "short".getBytes(java.nio.charset.StandardCharsets.UTF_8);

        byte[] ciphertext = GostKuznyechik.ctr(KEY, nonce, plaintext);

        assertThat(ciphertext).hasSize(plaintext.length).isNotEqualTo(plaintext);
        assertThat(GostKuznyechik.ctr(KEY, nonce, ciphertext)).isEqualTo(plaintext);
    }

    @Test
    void rejectsInvalidKeyAndNonce() {
        assertThatThrownBy(() -> GostKuznyechik.ctr(new byte[31], new byte[8], new byte[1]))
                .isInstanceOf(IllegalArgumentException.class);
        assertThatThrownBy(() -> GostKuznyechik.ctr(new byte[32], new byte[7], new byte[1]))
                .isInstanceOf(IllegalArgumentException.class);
    }
}
