package com.goping.security;

import com.goping.config.AppProperties;
import com.goping.error.ApiException;
import org.junit.jupiter.api.Test;

import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.time.ZoneId;
import java.time.ZoneOffset;
import java.util.List;
import java.util.UUID;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

class TokenServiceTest {

    private static final String SECRET = "unit-test-secret-0123456789abcdef0123456789abcdef";

    @Test
    void issuedTokenParsesBackToUserId() {
        TokenService service = new TokenService(properties(SECRET, "test-issuer", Duration.ofMinutes(15)));

        UUID userId = UUID.randomUUID();
        TokenService.Issued issued = service.issue(userId);

        assertThat(issued.token()).isNotBlank();
        assertThat(service.parse(issued.token())).isEqualTo(userId);
        assertThat(issued.expiresAt()).isAfter(Instant.now());
    }

    @Test
    void expiredTokenIsRejected() {
        MutableClock clock = new MutableClock(Instant.parse("2026-01-01T00:00:00Z"));
        TokenService service = new TokenService(
                properties(SECRET, "test-issuer", Duration.ofMinutes(15)), clock);

        String token = service.issue(UUID.randomUUID()).token();
        clock.advance(Duration.ofMinutes(16));

        assertThatThrownBy(() -> service.parse(token))
                .isInstanceOf(ApiException.class)
                .hasMessageContaining("Токен");
    }

    @Test
    void tokenFromAnotherIssuerIsRejected() {
        TokenService issuerA = new TokenService(properties(SECRET, "issuer-a", Duration.ofMinutes(15)));
        TokenService issuerB = new TokenService(properties(SECRET, "issuer-b", Duration.ofMinutes(15)));

        String token = issuerA.issue(UUID.randomUUID()).token();

        assertThatThrownBy(() -> issuerB.parse(token)).isInstanceOf(ApiException.class);
    }

    @Test
    void tamperedTokenIsRejected() {
        TokenService service = new TokenService(properties(SECRET, "test-issuer", Duration.ofMinutes(15)));
        String token = service.issue(UUID.randomUUID()).token();
        String tampered = token.substring(0, token.length() - 3) + "abc";

        assertThatThrownBy(() -> service.parse(tampered)).isInstanceOf(ApiException.class);
    }

    @Test
    void shortSecretIsRejected() {
        assertThatThrownBy(() -> properties("too-short", "test-issuer", Duration.ofMinutes(15)))
                .isInstanceOf(IllegalStateException.class)
                .hasMessageContaining("JWT_SECRET");
    }

    private static AppProperties properties(String secret, String issuer, Duration accessTtl) {
        return new AppProperties(
                "./data/test.db",
                secret,
                issuer,
                accessTtl,
                Duration.ofDays(30),
                "./data/uploads",
                "http://localhost:8080",
                5L * 1024 * 1024,
                30,
                120,
                List.of("*"),
                false,
                true,
                "",
                "",
                "",
                "",
                "",
                ""
        );
    }

    private static final class MutableClock extends Clock {

        private Instant instant;

        private MutableClock(Instant instant) {
            this.instant = instant;
        }

        private void advance(Duration duration) {
            instant = instant.plus(duration);
        }

        @Override
        public ZoneId getZone() {
            return ZoneOffset.UTC;
        }

        @Override
        public Clock withZone(ZoneId zone) {
            return this;
        }

        @Override
        public Instant instant() {
            return instant;
        }
    }
}
