package com.goping.crypto;

import com.goping.config.AppProperties;
import org.junit.jupiter.api.Test;

import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.time.Duration;
import java.util.List;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

class MessageCipherTest {

    private static AppProperties properties(String messageKey) {
        return new AppProperties(
                "./data/test.db",
                "test-secret-0123456789abcdef0123456789abcdef",
                "goping",
                Duration.ofMinutes(15),
                Duration.ofDays(30),
                "./data/uploads",
                "http://localhost:8080",
                5_242_880,
                30,
                120,
                List.of("*"),
                true,
                true,
                "",
                messageKey,
                "",
                "",
                "http://localhost/api/v1/auth/github/callback",
                "read:user"
        );
    }

    @Test
    void sealsAndOpensUtf8Text() throws Exception {
        MessageCipher cipher = new MessageCipher(properties(""));
        String text = "Привет, мир!\nСтрока 2 — ГОСТ 34.12-2015 🔒";

        MessageCipher.Sealed sealed = cipher.seal(text);

        assertThat(sealed.ciphertext()).isNotEqualTo(text.getBytes(StandardCharsets.UTF_8));
        assertThat(sealed.nonce()).hasSize(GostKuznyechik.NONCE_SIZE);
        assertThat(sealed.mac()).hasSize(32);
        assertThat(cipher.open(sealed.ciphertext(), sealed.nonce(), sealed.mac())).isEqualTo(text);
    }

    @Test
    void usesFreshNonceForEveryMessage() {
        MessageCipher cipher = new MessageCipher(properties(""));

        MessageCipher.Sealed first = cipher.seal("same text");
        MessageCipher.Sealed second = cipher.seal("same text");

        assertThat(first.nonce()).isNotEqualTo(second.nonce());
        assertThat(first.ciphertext()).isNotEqualTo(second.ciphertext());
    }

    @Test
    void rejectsTamperedCiphertext() {
        MessageCipher cipher = new MessageCipher(properties(""));
        MessageCipher.Sealed sealed = cipher.seal("important");

        byte[] tampered = sealed.ciphertext().clone();
        tampered[0] ^= 0x01;

        assertThatThrownBy(() -> cipher.open(tampered, sealed.nonce(), sealed.mac()))
                .isInstanceOf(GeneralSecurityException.class);
    }

    @Test
    void acceptsConfiguredHexKey() throws Exception {
        String hexKey = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
        MessageCipher cipher = new MessageCipher(properties(hexKey));
        MessageCipher.Sealed sealed = cipher.seal("configured key");

        assertThat(cipher.open(sealed.ciphertext(), sealed.nonce(), sealed.mac()))
                .isEqualTo("configured key");
    }

    @Test
    void rejectsConfiguredKeyOfWrongLength() {
        assertThatThrownBy(() -> new MessageCipher(properties("abcd")))
                .isInstanceOf(IllegalStateException.class)
                .hasMessageContaining("MESSAGE_KEY");
    }
}
