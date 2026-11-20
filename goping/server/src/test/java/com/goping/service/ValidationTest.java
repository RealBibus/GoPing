package com.goping.service;

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
        assertThat(Validation.username("goping").error()).contains("зарезервировано");
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
    void serverTokenFormatIsStrict() {
        assertThat(ServerTokenService.validFormat("GOPING-Ab3_x-9Qw2Z0")).isTrue();
        assertThat(ServerTokenService.validFormat("GOPING-abcdefghijkl")).isTrue();
        assertThat(ServerTokenService.validFormat("goping-abcdefghijkl")).isFalse();
        assertThat(ServerTokenService.validFormat("GOPING-abcdefghijk")).isFalse();
        assertThat(ServerTokenService.validFormat("GOPING-abcdefghijklm")).isFalse();
        assertThat(ServerTokenService.validFormat("GOPING-abcdefghij!l")).isFalse();
        assertThat(ServerTokenService.validFormat(null)).isFalse();
    }
}
