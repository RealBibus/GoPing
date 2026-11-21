package com.goping.realtime;

import com.goping.error.ApiException;
import com.goping.security.TokenService;
import org.springframework.http.server.ServerHttpRequest;
import org.springframework.http.server.ServerHttpResponse;
import org.springframework.http.server.ServletServerHttpRequest;
import org.springframework.stereotype.Component;
import org.springframework.web.socket.WebSocketHandler;
import org.springframework.web.socket.server.HandshakeInterceptor;

import java.util.Map;
import java.util.UUID;

@Component
public class WsAuthInterceptor implements HandshakeInterceptor {

    private final TokenService tokens;

    public WsAuthInterceptor(TokenService tokens) {
        this.tokens = tokens;
    }

    @Override
    public boolean beforeHandshake(ServerHttpRequest request,
                                   ServerHttpResponse response,
                                   WebSocketHandler wsHandler,
                                   Map<String, Object> attributes) {
        String token = null;
        if (request instanceof ServletServerHttpRequest servletRequest) {
            token = servletRequest.getServletRequest().getParameter("token");
        }
        if (token == null || token.isBlank()) {
            String query = request.getURI().getQuery();
            if (query != null) {
                for (String part : query.split("&")) {
                    int eq = part.indexOf('=');
                    if (eq > 0 && "token".equals(part.substring(0, eq))) {
                        token = part.substring(eq + 1);
                        break;
                    }
                }
            }
        }
        if (token == null || token.isBlank()) {
            return false;
        }
        try {
            UUID userId = tokens.parse(token);
            attributes.put(RealtimeWebSocketHandler.ATTR_USER_ID, userId);
            return true;
        } catch (ApiException e) {
            return false;
        }
    }

    @Override
    public void afterHandshake(ServerHttpRequest request,
                               ServerHttpResponse response,
                               WebSocketHandler wsHandler,
                               Exception exception) {
    }
}
