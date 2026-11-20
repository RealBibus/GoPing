package com.aerisflow.messenger.security;

import com.aerisflow.messenger.config.AppProperties;
import com.aerisflow.messenger.error.ApiException;
import io.jsonwebtoken.Claims;
import io.jsonwebtoken.JwtException;
import io.jsonwebtoken.Jwts;
import io.jsonwebtoken.security.Keys;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.stereotype.Service;

import javax.crypto.SecretKey;
import java.nio.charset.StandardCharsets;
import java.time.Clock;
import java.time.Instant;
import java.util.Date;
import java.util.UUID;

/**
 * Access-токены: JWT HS256, короткий срок жизни.
 */
@Service
public class TokenService {

    private final SecretKey key;
    private final String issuer;
    private final java.time.Duration ttl;
    private final Clock clock;

    public record Issued(String token, Instant expiresAt) {
    }

    @Autowired
    public TokenService(AppProperties properties) {
        this(properties, Clock.systemUTC());
    }

    TokenService(AppProperties properties, Clock clock) {
        this.key = Keys.hmacShaKeyFor(properties.jwtSecret().getBytes(StandardCharsets.UTF_8));
        this.issuer = properties.jwtIssuer();
        this.ttl = properties.accessTokenTtl();
        this.clock = clock;
    }

    public Issued issue(UUID userId) {
        Instant now = clock.instant();
        Instant expiresAt = now.plus(ttl);

        String token = Jwts.builder()
                .subject(userId.toString())
                .issuer(issuer)
                .issuedAt(Date.from(now))
                .notBefore(Date.from(now))
                .expiration(Date.from(expiresAt))
                .id(UUID.randomUUID().toString())
                .claim("token_use", "access")
                .signWith(key, Jwts.SIG.HS256)
                .compact();

        return new Issued(token, expiresAt);
    }

    public UUID parse(String rawToken) {
        try {
            Claims claims = Jwts.parser()
                    .verifyWith(key)
                    .requireIssuer(issuer)
                    .clockSkewSeconds(30)
                    .build()
                    .parseSignedClaims(rawToken)
                    .getPayload();

            if (!"access".equals(claims.get("token_use", String.class))) {
                throw ApiException.invalidToken();
            }
            return UUID.fromString(claims.getSubject());
        } catch (JwtException | IllegalArgumentException e) {
            throw ApiException.invalidToken();
        }
    }
}
