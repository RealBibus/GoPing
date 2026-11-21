package com.goping.crypto;

/**
 * ГОСТ Р 34.12-2015 «Кузнечик» (блок 128 бит, ключ 256 бит)
 * в режиме гаммирования CTR по ГОСТ Р 34.13-2015.
 *
 * <p>Реализация без внешних зависимостей. Для CTR нужен только прямой ход
 * блочного шифра, поэтому обратное преобразование здесь намеренно отсутствует.
 * Векторы из стандарта проверяются в {@code GostKuznyechikTest}.</p>
 */
public final class GostKuznyechik {

    public static final int BLOCK_SIZE = 16;
    public static final int KEY_SIZE = 32;
    public static final int NONCE_SIZE = 8;

    /** Таблица замен S (ГОСТ Р 34.12-2015, приложение А). */
    private static final int[] S_BOX = {
            0xFC, 0xEE, 0xDD, 0x11, 0xCF, 0x6E, 0x31, 0x16, 0xFB, 0xC4, 0xFA, 0xDA, 0x23, 0xC5, 0x04, 0x4D,
            0xE9, 0x77, 0xF0, 0xDB, 0x93, 0x2E, 0x99, 0xBA, 0x17, 0x36, 0xF1, 0xBB, 0x14, 0xCD, 0x5F, 0xC1,
            0xF9, 0x18, 0x65, 0x5A, 0xE2, 0x5C, 0xEF, 0x21, 0x81, 0x1C, 0x3C, 0x42, 0x8B, 0x01, 0x8E, 0x4F,
            0x05, 0x84, 0x02, 0xAE, 0xE3, 0x6A, 0x8F, 0xA0, 0x06, 0x0B, 0xED, 0x98, 0x7F, 0xD4, 0xD3, 0x1F,
            0xEB, 0x34, 0x2C, 0x51, 0xEA, 0xC8, 0x48, 0xAB, 0xF2, 0x2A, 0x68, 0xA2, 0xFD, 0x3A, 0xCE, 0xCC,
            0xB5, 0x70, 0x0E, 0x56, 0x08, 0x0C, 0x76, 0x12, 0xBF, 0x72, 0x13, 0x47, 0x9C, 0xB7, 0x5D, 0x87,
            0x15, 0xA1, 0x96, 0x29, 0x10, 0x7B, 0x9A, 0xC7, 0xF3, 0x91, 0x78, 0x6F, 0x9D, 0x9E, 0xB2, 0xB1,
            0x32, 0x75, 0x19, 0x3D, 0xFF, 0x35, 0x8A, 0x7E, 0x6D, 0x54, 0xC6, 0x80, 0xC3, 0xBD, 0x0D, 0x57,
            0xDF, 0xF5, 0x24, 0xA9, 0x3E, 0xA8, 0x43, 0xC9, 0xD7, 0x79, 0xD6, 0xF6, 0x7C, 0x22, 0xB9, 0x03,
            0xE0, 0x0F, 0xEC, 0xDE, 0x7A, 0x94, 0xB0, 0xBC, 0xDC, 0xE8, 0x28, 0x50, 0x4E, 0x33, 0x0A, 0x4A,
            0xA7, 0x97, 0x60, 0x73, 0x1E, 0x00, 0x62, 0x44, 0x1A, 0xB8, 0x38, 0x82, 0x64, 0x9F, 0x26, 0x41,
            0xAD, 0x45, 0x46, 0x92, 0x27, 0x5E, 0x55, 0x2F, 0x8C, 0xA3, 0xA5, 0x7D, 0x69, 0xD5, 0x95, 0x3B,
            0x07, 0x58, 0xB3, 0x40, 0x86, 0xAC, 0x1D, 0xF7, 0x30, 0x37, 0x6B, 0xE4, 0x88, 0xD9, 0xE7, 0x89,
            0xE1, 0x1B, 0x83, 0x49, 0x4C, 0x3F, 0xF8, 0xFE, 0x8D, 0x53, 0xAA, 0x90, 0xCA, 0xD8, 0x85, 0x61,
            0x20, 0x71, 0x67, 0xA4, 0x2D, 0x2B, 0x09, 0x5B, 0xCB, 0x9B, 0x25, 0xD0, 0xBE, 0xE5, 0x6C, 0x52,
            0x59, 0xA6, 0x74, 0xD2, 0xE6, 0xF4, 0xB4, 0xC0, 0xD1, 0x66, 0xAF, 0xC2, 0x39, 0x4B, 0x63, 0xB6
    };

    /** Коэффициенты линейного преобразования l из ГОСТ Р 34.12-2015. */
    private static final int[] L_COEFFICIENTS = {
            0x94, 0x20, 0x85, 0x10, 0xC2, 0xC0, 0x01, 0xFB,
            0x01, 0xC0, 0xC2, 0x10, 0x85, 0x20, 0x94, 0x01
    };

    /** Итерационные константы C1..C32. */
    private static final byte[][] CONSTANTS = buildConstants();

    private GostKuznyechik() {
    }

    /**
     * Шифрует/расшифровывает данные в режиме CTR.
     * Операция симметрична: один и тот же вызов выполняет и шифрование,
     * и расшифровку.
     *
     * @param key   ключ ровно 32 байта
     * @param nonce синхропосылка ровно 8 байт
     * @param input открытый или зашифрованный текст
     */
    public static byte[] ctr(byte[] key, byte[] nonce, byte[] input) {
        if (key == null || key.length != KEY_SIZE) {
            throw new IllegalArgumentException("GOST Kuznyechik key must be 32 bytes");
        }
        if (nonce == null || nonce.length != NONCE_SIZE) {
            throw new IllegalArgumentException("GOST CTR nonce must be 8 bytes");
        }
        if (input == null) {
            throw new IllegalArgumentException("input must not be null");
        }

        byte[][] roundKeys = expandKey(key);
        byte[] output = new byte[input.length];
        long counter = 0;
        for (int offset = 0; offset < input.length; offset += BLOCK_SIZE, counter++) {
            byte[] gamma = encryptBlock(counterBlock(nonce, counter), roundKeys);
            int length = Math.min(BLOCK_SIZE, input.length - offset);
            for (int i = 0; i < length; i++) {
                output[offset + i] = (byte) (input[offset + i] ^ gamma[i]);
            }
        }
        return output;
    }

    private static byte[] counterBlock(byte[] nonce, long counter) {
        byte[] block = new byte[BLOCK_SIZE];
        System.arraycopy(nonce, 0, block, 0, NONCE_SIZE);
        for (int i = 0; i < NONCE_SIZE; i++) {
            block[BLOCK_SIZE - 1 - i] = (byte) (counter >>> (8 * i));
        }
        return block;
    }

    private static byte[][] expandKey(byte[] key) {
        byte[][] roundKeys = new byte[10][];
        roundKeys[0] = slice(key, 0);
        roundKeys[1] = slice(key, 16);

        for (int i = 0; i < 4; i++) {
            byte[] left = roundKeys[2 * i].clone();
            byte[] right = roundKeys[2 * i + 1].clone();
            for (int j = 0; j < 8; j++) {
                byte[] transformed = lxs(left, CONSTANTS[8 * i + j]);
                byte[] nextLeft = xor(transformed, right);
                right = left;
                left = nextLeft;
            }
            roundKeys[2 * i + 2] = left;
            roundKeys[2 * i + 3] = right;
        }
        return roundKeys;
    }

    private static byte[] encryptBlock(byte[] block, byte[][] roundKeys) {
        byte[] state = block.clone();
        for (int round = 0; round < 9; round++) {
            state = lxs(state, roundKeys[round]);
        }
        return xor(state, roundKeys[9]);
    }

    private static byte[][] buildConstants() {
        byte[][] constants = new byte[32][];
        for (int i = 0; i < constants.length; i++) {
            byte[] value = new byte[BLOCK_SIZE];
            value[BLOCK_SIZE - 1] = (byte) (i + 1);
            constants[i] = linear(value);
        }
        return constants;
    }

    private static byte[] lxs(byte[] block, byte[] roundKey) {
        return linear(substitute(xor(block, roundKey)));
    }

    private static byte[] substitute(byte[] block) {
        byte[] result = new byte[BLOCK_SIZE];
        for (int i = 0; i < BLOCK_SIZE; i++) {
            result[i] = (byte) S_BOX[block[i] & 0xFF];
        }
        return result;
    }

    private static byte[] linear(byte[] block) {
        byte[] result = block.clone();
        for (int i = 0; i < BLOCK_SIZE; i++) {
            result = stepR(result);
        }
        return result;
    }

    /**
     * Одно применение преобразования R:
     * R(a15..a0) = l(a15..a0) || a15 || .. || a1.
     */
    private static byte[] stepR(byte[] block) {
        byte[] result = new byte[BLOCK_SIZE];
        int value = 0;
        for (int i = 0; i < BLOCK_SIZE; i++) {
            value ^= multiply(block[i] & 0xFF, L_COEFFICIENTS[i]);
        }
        result[0] = (byte) value;
        System.arraycopy(block, 0, result, 1, BLOCK_SIZE - 1);
        return result;
    }

    /** Умножение в GF(2^8) по модулю x^8 + x^7 + x^6 + x + 1. */
    private static int multiply(int a, int b) {
        int product = 0;
        for (int i = 0; i < 8; i++) {
            if ((b & 1) != 0) {
                product ^= a;
            }
            boolean highBit = (a & 0x80) != 0;
            a = (a << 1) & 0xFF;
            if (highBit) {
                a ^= 0xC3;
            }
            b >>= 1;
        }
        return product & 0xFF;
    }

    private static byte[] xor(byte[] left, byte[] right) {
        byte[] result = new byte[BLOCK_SIZE];
        for (int i = 0; i < BLOCK_SIZE; i++) {
            result[i] = (byte) (left[i] ^ right[i]);
        }
        return result;
    }

    private static byte[] slice(byte[] source, int offset) {
        byte[] result = new byte[BLOCK_SIZE];
        System.arraycopy(source, offset, result, 0, BLOCK_SIZE);
        return result;
    }
}
