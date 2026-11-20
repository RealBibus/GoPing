package com.aerisflow.messenger.web.filter;

import com.aerisflow.messenger.error.ApiException;
import com.aerisflow.messenger.security.RequestContext;
import com.aerisflow.messenger.security.TokenService;
import com.aerisflow.messenger.web.ErrorResponseWriter;
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
import java.util.Set;
import java.util.UUID;

/**
 * Проверяет Bearer-токен для всего /api/v1, кроме публичных маршрутов.
 */
@Component
@Order(Ordered.HIGHEST_PRECEDENCE + 3)
public class AuthFilter extends OncePerRequestFilter {

    private static final Set<String> PUBLIC_PATHS = Set.of(
            "/api/v1/auth/register",
            "/api/v1/auth/login",
            "/api/v1/auth/refresh",
            "/api/v1/auth/logout",
            "/api/v1/users/username-available"
    );

    private final TokenService tokens;
    private final ObjectMapper objectMapper;

    public AuthFilter(TokenService tokens, ObjectMapper objectMapper) {
        this.tokens = tokens;
        this.objectMapper = objectMapper;
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
        if (!path.startsWith("/api/v1/") || PUBLIC_PATHS.contains(path)) {
            filterChain.doFilter(request, response);
            return;
        }

        String header = request.getHeader("Authorization");
        if (header == null || !header.regionMatches(true, 0, "Bearer ", 0, 7)) {
            unauthorized(request, response);
            return;
        }

        String rawToken = header.substring(7).trim();
        if (rawToken.isEmpty()) {
            unauthorized(request, response);
            return;
        }

        try {
            UUID userId = tokens.parse(rawToken);
            request.setAttribute(RequestContext.USER_ID_ATTRIBUTE, userId);
        } catch (ApiException e) {
            ErrorResponseWriter.write(response, objectMapper, HttpStatus.UNAUTHORIZED,
                    e.code(), e.getMessage(), e.details(), RequestContext.requestId(request));
            return;
        }

        filterChain.doFilter(request, response);
    }

    private void unauthorized(HttpServletRequest request, HttpServletResponse response) throws IOException {
        ErrorResponseWriter.write(response, objectMapper, HttpStatus.UNAUTHORIZED,
                "UNAUTHORIZED", "Требуется авторизация", null, RequestContext.requestId(request));
    }
}
