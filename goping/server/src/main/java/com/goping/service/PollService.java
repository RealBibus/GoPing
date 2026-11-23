package com.goping.service;

import com.fasterxml.jackson.databind.ObjectMapper;
import com.goping.domain.Message;
import com.goping.error.ApiException;
import com.goping.error.ValidationException;
import com.goping.repository.ChatRepository;
import com.goping.repository.PollRepository;
import com.goping.repository.PollRepository.OptionRow;
import com.goping.repository.PollRepository.PollRow;
import com.goping.web.dto.ApiDtos;
import org.springframework.http.HttpStatus;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import java.io.IOException;
import java.time.Instant;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;

/** Poll creation and voting. Poll cards are ordinary encrypted messages. */
@Service
public class PollService {

    private static final int MAX_QUESTION_LENGTH = 500;
    private static final int MAX_OPTION_LENGTH = 200;
    private static final ObjectMapper JSON = new ObjectMapper();

    private final PollRepository polls;
    private final ChatRepository chats;
    private final MessageService messages;

    public record CreatedPoll(ApiDtos.PollDto poll, Message message) {
    }

    public PollService(PollRepository polls, ChatRepository chats, MessageService messages) {
        this.polls = polls;
        this.chats = chats;
        this.messages = messages;
    }

    @Transactional
    public CreatedPoll create(UUID userId, UUID chatId, String rawQuestion, List<String> rawOptions) {
        requireMember(userId, chatId);
        String question = text(rawQuestion, "question", MAX_QUESTION_LENGTH);
        List<String> options = options(rawOptions);
        String createdAt = Instant.now().toString();
        String pollId = UUID.randomUUID().toString();
        polls.create(pollId, chatId.toString(), userId.toString(), question, createdAt);
        for (int i = 0; i < options.size(); i++) {
            polls.addOption(UUID.randomUUID().toString(), pollId, i, options.get(i));
        }

        PollRow row = polls.findById(pollId).orElseThrow(ApiException::notFound);
        ApiDtos.PollDto poll = dto(row, null);
        Message message = messages.storePayloadMessage(userId, chatId, payload(poll), "poll");
        return new CreatedPoll(poll, message);
    }

    @Transactional
    public ApiDtos.PollDto vote(UUID userId, UUID chatId, String pollId, String optionId) {
        requireMember(userId, chatId);
        PollRow row = requirePoll(chatId, pollId);
        if (row.closed()) {
            throw new ApiException(HttpStatus.BAD_REQUEST, "POLL_CLOSED", "Опрос закрыт");
        }
        String safeOptionId = optionId == null ? "" : optionId.trim();
        if (safeOptionId.isEmpty()) {
            throw new ValidationException(Map.of("optionId", "выберите вариант"));
        }
        if (!polls.hasOption(pollId, safeOptionId)) {
            throw new ValidationException(Map.of("optionId", "вариант не найден"));
        }
        polls.upsertVote(pollId, safeOptionId, userId.toString(), Instant.now().toString());
        return dto(row, polls.myVote(pollId, userId.toString()));
    }

    public ApiDtos.PollDto get(UUID userId, UUID chatId, String pollId) {
        requireMember(userId, chatId);
        PollRow row = requirePoll(chatId, pollId);
        return dto(row, polls.myVote(pollId, userId.toString()));
    }

    private void requireMember(UUID userId, UUID chatId) {
        if (!chats.isMember(chatId, userId)) {
            throw ApiException.notFound();
        }
    }

    private PollRow requirePoll(UUID chatId, String pollId) {
        PollRow row = polls.findById(pollId == null ? "" : pollId)
                .orElseThrow(ApiException::notFound);
        if (!row.chatId().equals(chatId.toString())) {
            throw ApiException.notFound();
        }
        return row;
    }

    private ApiDtos.PollDto dto(PollRow row, String myVoteOptionId) {
        List<OptionRow> stored = polls.options(row.id());
        Map<String, Integer> counts = polls.voteCounts(row.id());
        List<ApiDtos.PollOptionDto> options = new ArrayList<>(stored.size());
        int total = 0;
        for (OptionRow option : stored) {
            int votes = counts.getOrDefault(option.id(), 0);
            total += votes;
            options.add(new ApiDtos.PollOptionDto(option.id(), option.text(), votes));
        }
        return new ApiDtos.PollDto(
                row.id(),
                row.question(),
                List.copyOf(options),
                total,
                row.closed(),
                myVoteOptionId != null,
                myVoteOptionId);
    }

    /** Payload stored as the encrypted text of a poll message. */
    private static String payload(ApiDtos.PollDto poll) {
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("pollId", poll.id());
        body.put("question", poll.question());
        List<Map<String, Object>> options = new ArrayList<>(poll.options().size());
        for (ApiDtos.PollOptionDto option : poll.options()) {
            Map<String, Object> item = new LinkedHashMap<>();
            item.put("id", option.id());
            item.put("text", option.text());
            options.add(item);
        }
        body.put("options", options);
        try {
            return JSON.writeValueAsString(body);
        } catch (IOException e) {
            throw new IllegalStateException("failed to build poll payload", e);
        }
    }

    private static List<String> options(List<String> rawOptions) {
        if (rawOptions == null || rawOptions.size() < 2 || rawOptions.size() > 10) {
            throw new ValidationException(Map.of("options", "нужно от 2 до 10 вариантов"));
        }
        List<String> result = new ArrayList<>(rawOptions.size());
        for (String raw : rawOptions) {
            result.add(text(raw, "options", MAX_OPTION_LENGTH));
        }
        return result;
    }

    private static String text(String raw, String field, int maxLength) {
        String value = raw == null ? "" : raw.strip();
        if (value.isEmpty() || value.codePointCount(0, value.length()) > maxLength) {
            throw new ValidationException(Map.of(field, "некорректное значение"));
        }
        for (int i = 0; i < value.length(); ) {
            int codePoint = value.codePointAt(i);
            if (Character.isISOControl(codePoint)) {
                throw new ValidationException(Map.of(field, "некорректное значение"));
            }
            i += Character.charCount(codePoint);
        }
        return value;
    }
}
