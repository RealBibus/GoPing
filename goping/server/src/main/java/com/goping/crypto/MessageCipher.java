package com.goping.crypto;

import com.goping.config.AppProperties;
import org.springframework.stereotype.Component;

import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.util.Base64;
import java.util.HexFormat;

/**
 * Шифрование сообщений алгоритмом, допущенным к применению в РФ:
 * ГОСТ Р 34.12-2015 «Кузнечик» в режиме CTR (ГОСТ Р 34.13-2015).
 *
 * <p>Целостность защищена HMAC-SHA256 (encrypt-then-MAC): без проверки тега
 * сообщение не расшифровывается. Ключ шифрования и ключ MAC выводятся из
 * {@code MESSAGE_KEY}, а если он не задан — из {@code JWT_SECRET} с разными
 * метками домена.</p>
 */
@Component
public class MessageCipher {

    /** Стабильный идентификатор схемы для API и БД. */
    public static final String ALGORITHM = "GOST34.12-2015/KUZNYECHIK/CTR";

    private static final int NONCE_BYTES = GostKuznyechik.NONCE_SIZE;
    private static final int MAC_BYTES = 32;

    private final byte[] encryptionKey;
    private final byte[] authenticationKey;
    private final SecureRandom random = new SecureRandom();

    public record Sealed(byte[] ciphertext, byte[] nonce, byte[] mac) {
    }

    public MessageCipher(AppProperties properties) {
        byte[] material = keyMaterial(properties);
        this.encryptionKey = derive(material, "goping/message-encryption/v1");
        this.authenticationKey = derive(material, "goping/message-authentication/v1");
    }

    public Sealed seal(String plaintext) {
        byte[] nonce = new byte[NONCE_BYTES];
        random.nextBytes(nonce);
        byte[] ciphertext = GostKuznyechik.ctr(
                encryptionKey, nonce, plaintext.getBytes(StandardCharsets.UTF_8));
        return new Sealed(ciphertext, nonce, hmac(nonce, ciphertext));
    }

    public String open(byte[] ciphertext, byte[] nonce, byte[] mac) throws GeneralSecurityException {
        if (nonce == null || nonce.length != NONCE_BYTES) {
            throw new GeneralSecurityException("invalid message nonce");
        }
        if (mac == null || mac.length != MAC_BYTES) {
            throw new GeneralSecurityException("invalid message authentication code");
        }
        if (!MessageDigest.isEqual(hmac(nonce, ciphertext), mac)) {
            throw new GeneralSecurityException("message authentication code mismatch");
        }
        return new String(GostKuznyechik.ctr(encryptionKey, nonce, ciphertext), StandardCharsets.UTF_8);
    }

    private byte[] hmac(byte[] nonce, byte[] ciphertext) {
        try {
            Mac mac = Mac.getInstance("HmacSHA256");
            mac.init(new SecretKeySpec(authenticationKey, "HmacSHA256"));
            mac.update(nonce);
            mac.update(ciphertext);
            return mac.doFinal();
        } catch (GeneralSecurityException e) {
            throw new IllegalStateException("HmacSHA256 is not available", e);
        }
    }

    private static byte[] keyMaterial(AppProperties properties) {
        String configured = properties.messageKey();
        if (configured != null && !configured.isBlank()) {
            return parseConfiguredKey(configured.trim());
        }
        // Работает «из коробки»: ключ выводится из JWT_SECRET. Если секрет
        // поменять, старые сообщения перестанут расшифровываться — для
        // постоянного ключа задайте MESSAGE_KEY.
        return properties.jwtSecret().getBytes(StandardCharsets.UTF_8);
    }

    private static byte[] parseConfiguredKey(String value) {
        byte[] parsed;
        try {
            if (value.matches("(?i)[0-9a-f]{64}")) {
                parsed = HexFormat.of().parseHex(value);
            } else {
                parsed = Base64.getDecoder().decode(value);
            }
        } catch (IllegalArgumentException e) {
            throw new IllegalStateException(
                    "MESSAGE_KEY must be 64 hex characters or Base64 of 32 bytes", e);
        }
        if (parsed.length != GostKuznyechik.KEY_SIZE) {
            throw new IllegalStateException("MESSAGE_KEY must decode to 32 bytes");
        }
        return parsed;
    }

    private static byte[] derive(byte[] material, String label) {
        try {
            MessageDigest digest = MessageDigest.getInstance("SHA-256");
            digest.update(label.getBytes(StandardCharsets.UTF_8));
            digest.update((byte) 0);
            digest.update(material);
            return digest.digest();
        } catch (GeneralSecurityException e) {
            throw new IllegalStateException("SHA-256 is not available", e);
        }
    }
}
