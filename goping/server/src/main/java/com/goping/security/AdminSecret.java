package com.goping.security;

import com.goping.config.AppProperties;
import com.goping.error.ApiException;
import org.springframework.stereotype.Component;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;

/** Shared X-Admin-Secret check for every admin endpoint. */
@Component
public class AdminSecret {

    private final AppProperties properties;

    public AdminSecret(AppProperties properties) {
        this.properties = properties;
    }

    public void require(String secret) {
        if (!properties.adminEnabled()) {
            throw ApiException.adminDisabled();
        }
        byte[] expected = properties.adminSecret().getBytes(StandardCharsets.UTF_8);
        byte[] actual = (secret == null ? "" : secret).getBytes(StandardCharsets.UTF_8);
        if (!MessageDigest.isEqual(expected, actual)) {
            throw ApiException.adminUnauthorized();
        }
    }
}
