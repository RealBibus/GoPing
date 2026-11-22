package com.goping.web;

import com.goping.security.RequestContext;
import jakarta.servlet.http.HttpServletRequest;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** Настройки ICE для WebRTC-звонков. Команда ws-сигналинга остаётся в RealtimeWebSocketHandler. */
@RestController
@RequestMapping("/api/v1/rtc")
public class RtcController {

    @Value("${app.turn.urls:}")
    private String turnUrls;

    @Value("${app.turn.username:}")
    private String turnUsername;

    @Value("${app.turn.credential:}")
    private String turnCredential;

    @GetMapping("/config")
    public Map<String, Object> config(HttpServletRequest request) {
        RequestContext.userId(request);

        List<Map<String, Object>> iceServers = new ArrayList<>();
        Map<String, Object> stun = new LinkedHashMap<>();
        stun.put("urls", List.of("stun:stun.l.google.com:19302", "stun:stun1.l.google.com:19302"));
        iceServers.add(stun);

        if (turnUrls != null && !turnUrls.isBlank()) {
            List<String> urls = new ArrayList<>();
            for (String raw : turnUrls.split(",")) {
                String url = raw.trim();
                if (!url.isEmpty()) {
                    urls.add(url);
                }
            }
            if (!urls.isEmpty()) {
                Map<String, Object> turn = new LinkedHashMap<>();
                turn.put("urls", urls);
                turn.put("username", turnUsername == null ? "" : turnUsername);
                turn.put("credential", turnCredential == null ? "" : turnCredential);
                iceServers.add(turn);
            }
        }

        Map<String, Object> response = new LinkedHashMap<>();
        response.put("iceServers", iceServers);
        return response;
    }
}
