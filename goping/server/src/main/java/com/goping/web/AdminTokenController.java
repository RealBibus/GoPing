package com.goping.web;

import com.goping.error.ApiException;
import com.goping.repository.ServerTokenRepository.StoredServerToken;
import com.goping.security.AdminSecret;
import com.goping.service.ServerTokenService;
import com.goping.web.dto.ApiDtos;
import org.springframework.http.HttpStatus;
import org.springframework.web.bind.annotation.DeleteMapping;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestHeader;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.ResponseStatus;
import org.springframework.web.bind.annotation.RestController;

import java.util.List;
import java.util.UUID;

/**
 * Выдача и отзыв серверных токенов. Защищено ADMIN_SECRET,
 * а не пользовательской сессией.
 */
@RestController
@RequestMapping("/api/v1/admin/tokens")
public class AdminTokenController {

    private final ServerTokenService tokens;
    private final AdminSecret adminSecret;

    public AdminTokenController(ServerTokenService tokens, AdminSecret adminSecret) {
        this.tokens = tokens;
        this.adminSecret = adminSecret;
    }

    @PostMapping
    @ResponseStatus(HttpStatus.CREATED)
    public ApiDtos.AdminTokenIssued create(@RequestHeader(value = "X-Admin-Secret", required = false) String secret,
                                           @RequestBody(required = false) ApiDtos.CreateServerTokenRequest body) {
        adminSecret.require(secret);
        String label = body == null ? "" : body.label();
        ServerTokenService.IssuedToken issued = tokens.issue(label);
        return new ApiDtos.AdminTokenIssued(dto(issued.stored()), issued.value());
    }

    @GetMapping
    public ApiDtos.AdminTokenListResponse list(
            @RequestHeader(value = "X-Admin-Secret", required = false) String secret) {
        adminSecret.require(secret);
        List<ApiDtos.AdminTokenDto> items = tokens.list().stream().map(AdminTokenController::dto).toList();
        return new ApiDtos.AdminTokenListResponse(items);
    }

    @DeleteMapping("/{id}")
    @ResponseStatus(HttpStatus.NO_CONTENT)
    public void revoke(@RequestHeader(value = "X-Admin-Secret", required = false) String secret,
                       @PathVariable String id) {
        adminSecret.require(secret);
        UUID tokenId;
        try {
            tokenId = UUID.fromString(id);
        } catch (IllegalArgumentException e) {
            throw new ApiException(HttpStatus.BAD_REQUEST, "INVALID_ID", "Некорректный идентификатор токена");
        }
        tokens.revoke(tokenId);
    }

    private static ApiDtos.AdminTokenDto dto(StoredServerToken token) {
        return new ApiDtos.AdminTokenDto(
                token.id().toString(),
                token.prefix(),
                token.label(),
                token.userId() == null ? null : token.userId().toString(),
                token.createdAt(),
                token.activatedAt(),
                token.lastUsedAt(),
                token.revokedAt()
        );
    }
}
