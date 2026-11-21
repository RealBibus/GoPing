package com.goping.config;

import org.springframework.stereotype.Component;

/**
 * Разрешение удалять чужие сообщения. Значение берётся из настроек сервера,
 * но может меняться без перезапуска (используется и тестами, и админ-кодом).
 */
@Component
public class DeletePolicy {

    private volatile boolean allowDeleteOthers;

    public DeletePolicy(AppProperties properties) {
        this.allowDeleteOthers = properties.allowDeleteOthersMessages();
    }

    public boolean isAllowDeleteOthers() {
        return allowDeleteOthers;
    }

    public void setAllowDeleteOthers(boolean allowDeleteOthers) {
        this.allowDeleteOthers = allowDeleteOthers;
    }
}
