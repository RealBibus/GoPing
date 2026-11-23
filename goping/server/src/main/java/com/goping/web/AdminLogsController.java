package com.goping.web;

import com.goping.security.AdminSecret;
import com.goping.service.ServerLogBuffer;
import com.goping.web.dto.ApiDtos;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RequestHeader;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;

/** Recent in-memory server logs. Protected by X-Admin-Secret. */
@RestController
@RequestMapping("/api/v1/admin/logs")
public class AdminLogsController {

    private final AdminSecret adminSecret;
    private final ServerLogBuffer buffer;

    public AdminLogsController(AdminSecret adminSecret, ServerLogBuffer buffer) {
        this.adminSecret = adminSecret;
        this.buffer = buffer;
    }

    @GetMapping
    public ApiDtos.AdminLogsResponse lines(
            @RequestHeader(value = "X-Admin-Secret", required = false) String secret,
            @RequestParam(defaultValue = "200") int lines) {
        adminSecret.require(secret);
        return new ApiDtos.AdminLogsResponse(buffer.recent(lines));
    }
}
