package com.goping.config;

import com.goping.realtime.RealtimeWebSocketHandler;
import com.goping.realtime.WsAuthInterceptor;
import org.springframework.context.annotation.Configuration;
import org.springframework.web.socket.config.annotation.EnableWebSocket;
import org.springframework.web.socket.config.annotation.WebSocketConfigurer;
import org.springframework.web.socket.config.annotation.WebSocketHandlerRegistry;

@Configuration
@EnableWebSocket
public class WebSocketConfig implements WebSocketConfigurer {

    private final RealtimeWebSocketHandler handler;
    private final WsAuthInterceptor auth;

    public WebSocketConfig(RealtimeWebSocketHandler handler, WsAuthInterceptor auth) {
        this.handler = handler;
        this.auth = auth;
    }

    @Override
    public void registerWebSocketHandlers(WebSocketHandlerRegistry registry) {
        registry.addHandler(handler, "/api/v1/ws")
                .addInterceptors(auth)
                .setAllowedOrigins("*");
    }
}
