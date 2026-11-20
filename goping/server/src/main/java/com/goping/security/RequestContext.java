package com.goping.security;

import com.goping.error.ApiException;
import jakarta.servlet.http.HttpServletRequest;

import java.util.UUID;

/**
 * Данные запроса, которые проставляют фильтры.
 */
public final class RequestContext {

    public static final String REQUEST_ID_ATTRIBUTE = "requestId";
    public static final String USER_ID_ATTRIBUTE = "userId";

    private RequestContext() {
    }

    public static String requestId(HttpServletRequest request) {
        Object value = request.getAttribute(REQUEST_ID_ATTRIBUTE);
        return value instanceof String id ? id : null;
    }

    public static UUID userId(HttpServletRequest request) {
        Object value = request.getAttribute(USER_ID_ATTRIBUTE);
        if (value instanceof UUID id) {
            return id;
        }
        throw ApiException.unauthorized();
    }

    public static String clientIp(HttpServletRequest request) {
        String remote = request.getRemoteAddr();
        return remote == null ? "" : remote;
    }
}
