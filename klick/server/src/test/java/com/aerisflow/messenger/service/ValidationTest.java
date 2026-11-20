package com.aerisflow.messenger.service;

import org.junit.jupiter.api.Test;

import static org.assertj.core.api.Assertions.assertThat;

class ValidationTest {

    @Test
    void usernameIsNormalizedAndValidated() {
        assertThat(Validation.username("  Alice_01  ").value()).isEqualTo("alice_01");
        assertThat(Validation.username("  Alice_01  ").hasError()).isFalse();

        assertThat(Validation.username("ab").error()).contains("3–32");
        assertThat(Validation.username("1alice").hasError()).isTrue();
        assertThat(Validation.username("alice-01").hasError()).isTrue();
        assertThat(Validation.username("admin").error()).contains("зарезервировано");
        assertThat(Validation.username("a".repeat(33)).hasError()).isTrue();
    }

    @Test
    void nicknameRejectsEmptyAndTooLongButAllowsSpaces() {
        assertThat(Validation.nickname("  Лиса  ").value()).isEqualTo("Лиса");
        assertThat(Validation.nickname("   ").hasError()).isTrue();
        assertThat(Validation.nickname("я".repeat(51)).hasError()).isTrue();
        assertThat(Validation.nickname("я".repeat(50)).hasError()).isFalse();
        assertThat(Validation.nickname("bad\u0000name").hasError()).isTrue();
    }

    @Test
    void bioAllowsNewlinesAndLimitsLength() {
        assertThat(Validation.bio("первая\nвторая").hasError()).isFalse();
        assertThat(Validation.bio("x".repeat(301)).hasError()).isTrue();
        assertThat(Validation.bio("x".repeat(300)).hasError()).isFalse();
        assertThat(Validation.bio("bad\u0007bell").hasError()).isTrue();
    }

    @Test
    void emailIsLowercasedAndValidated() {
        assertThat(Validation.email("  User@Example.COM ").value()).isEqualTo("user@example.com");
        assertThat(Validation.email("user@example.com").hasError()).isFalse();
        assertThat(Validation.email("not-an-email").hasError()).isTrue();
        assertThat(Validation.email("").hasError()).isTrue();
        assertThat(Validation.email("a".repeat(250) + "@example.com").hasError()).isTrue();
    }

    @Test
    void passwordRequiresEightCharactersAndAtMost72Bytes() {
        assertThat(Validation.password("12345678").hasError()).isFalse();
        assertThat(Validation.password("1234567").hasError()).isTrue();
        assertThat(Validation.password("я".repeat(37)).error()).contains("72");
        assertThat(Validation.password(null).hasError()).isTrue();
    }
}
