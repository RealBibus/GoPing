package com.goping.web;

import com.goping.config.LiveLimits;
import com.goping.realtime.RealtimeEvents;
import com.goping.security.AdminSecret;
import com.goping.web.dto.ApiDtos;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestHeader;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

/** Runtime limit changes. Protected by X-Admin-Secret, not by a user token. */
@RestController
@RequestMapping("/api/v1/admin/limits")
public class AdminLimitsController {

    private final AdminSecret adminSecret;
    private final LiveLimits limits;
    private final RealtimeEvents events;

    public AdminLimitsController(AdminSecret adminSecret, LiveLimits limits, RealtimeEvents events) {
        this.adminSecret = adminSecret;
        this.limits = limits;
        this.events = events;
    }

    @PostMapping
    public ApiDtos.LimitsDto update(
            @RequestHeader(value = "X-Admin-Secret", required = false) String secret,
            @RequestBody(required = false) ApiDtos.UpdateLimitsRequest body) {
        adminSecret.require(secret);
        LiveLimits.Update update = body == null ? null : new LiveLimits.Update(
                body.maxTextLength(), body.maxVoiceSeconds(), body.maxCallMinutes(), body.maxFileBytes());
        LiveLimits.Limits updated = limits.update(update);
        events.limitsChanged();
        return ClientConfigController.dto(updated);
    }
}
