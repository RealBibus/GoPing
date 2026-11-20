package com.goping.web.filter;

import com.goping.config.AppProperties;
import com.goping.security.RequestContext;
import com.goping.web.ErrorResponseWriter;
import com.fasterxml.jackson.databind.ObjectMapper;
import jakarta.servlet.FilterChain;
import jakarta.servlet.ServletException;
import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;
import org.springframework.core.Ordered;
import org.springframework.core.annotation.Order;
import org.springframework.http.HttpStatus;
import org.springframework.stereotype.Component;
import org.springframework.web.filter.OncePerRequestFilter;

import java.io.IOException;
import java.util.HashMap;
import java.util.Map;
import java.util.Set;

/**
 * Простой лимитер в памяти для auth-запросов и проверки username.
 */
@Component
@Order(Ordered.HIGHEST_PRECEDENCE + 2)
public class RateLimitFilter extends OncePerRequestFilter {

    private static final Set<String> AUTH_PATHS = Set.of(
            "/api/v1/auth/token",
            "/api/v1/auth/refresh",
            "/api/v1/auth/logout",
            "/api/v1/auth/github/start",
            "/api/v1/auth/github/callback",
            "/api/v1/auth/github/poll"
    );
    private static final long WINDOW_MS = 60_000;

    private final ObjectMapper objectMapper;
    private final int authLimit;
    private final int availabilityLimit;
    private final Map<String, Window> windows = new HashMap<>();

    public RateLimitFilter(ObjectMapper objectMapper, AppProperties properties) {
        this.objectMapper = objectMapper;
        this.authLimit = properties.authRateLimit();
        this.availabilityLimit = properties.availabilityRateLimit();
    }

    @Override
    protected void doFilterInternal(HttpServletRequest request,
                                    HttpServletResponse response,
                                    FilterChain filterChain) throws ServletException, IOException {
        if ("OPTIONS".equalsIgnoreCase(request.getMethod())) {
            filterChain.doFilter(request, response);
            return;
        }

        String path = request.getRequestURI();
        String bucket = null;
        int limit = 0;

        if (AUTH_PATHS.contains(path) || path.startsWith("/api/v1/admin/")) {
            bucket = "auth";
            limit = authLimit;
        } else if ("GET".equalsIgnoreCase(request.getMethod())
                && path.equals("/api/v1/users/username-available")) {
            bucket = "availability";
            limit = availabilityLimit;
        }

        if (bucket == null) {
            filterChain.doFilter(request, response);
            return;
        }

        String key = bucket + ":" + RequestContext.clientIp(request);
        if (!allow(key, limit)) {
            response.setHeader("Retry-After", "60");
            ErrorResponseWriter.write(response, objectMapper, HttpStatus.TOO_MANY_REQUESTS,
                    "RATE_LIMITED", "Слишком много запросов. Попробуйте позже.", null,
                    RequestContext.requestId(request));
            return;
        }

        filterChain.doFilter(request, response);
    }

    private boolean allow(String key, int limit) {
        long now = System.currentTimeMillis();
        synchronized (windows) {
            Window window = windows.get(key);
            if (window == null || now - window.start >= WINDOW_MS) {
                window = new Window(now, 0);
                windows.put(key, window);
            }
            if (window.count >= limit) {
                return false;
            }
            window.count++;

            if (windows.size() > 10_000) {
                windows.entrySet().removeIf(entry -> now - entry.getValue().start > 2 * WINDOW_MS);
            }
            return true;
        }
    }

    private static final class Window {
        private final long start;
        private int count;

        private Window(long start, int count) {
            this.start = start;
            this.count = count;
        }
    }
}
