package com.goping.web;

import com.goping.security.RequestContext;
import com.goping.service.PresenceService;
import jakarta.servlet.http.HttpServletRequest;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

@RestController
@RequestMapping("/api/v1/presence")
public class PresenceController {

    private final PresenceService presence;

    public PresenceController(PresenceService presence) {
        this.presence = presence;
    }

    @PostMapping("/heartbeat")
    public ResponseEntity<Void> heartbeat(HttpServletRequest request) {
        presence.heartbeat(RequestContext.userId(request));
        return ResponseEntity.noContent().build();
    }
}
