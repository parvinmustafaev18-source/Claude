#!/bin/sh
# Напоминание о единственной рабочей линии. Вызывается хуком SessionStart:
# то, что он печатает, попадает в контекст сессии Claude ДО первой правки.
TARGET=last-version
echo "ПРАВИЛО ВЕТОК (нарушалось 17.10.2026, стоило пользователю дня):"
echo "  Единственная рабочая линия — $TARGET. На рабочем компьютере стоит сборка"
echo "  с неё, update.bat перематывает только её. Правка, положенная в main или в"
echo "  ветку claude/* от main, до пользователя НЕ доедет, а сборка с неё отнимет"
echo "  у него команды LIRZONE/LIRMARK/LIRSPLIT/LIRSHORT, которых в линии main нет."
echo "  Линия main закрыта: LIRTHICK и LIRREMESH в работу не берём."
echo "  Новая команда или исправление делается НА $TARGET и туда же пушится."
if command -v git >/dev/null 2>&1; then
    HEAD_BR=$(git rev-parse --abbrev-ref HEAD 2>/dev/null)
    echo "  Текущая ветка: ${HEAD_BR:-неизвестна}"
    git fetch -q origin "$TARGET" >/dev/null 2>&1
    if git rev-parse --verify -q FETCH_HEAD >/dev/null 2>&1; then
        if git merge-base --is-ancestor FETCH_HEAD HEAD >/dev/null 2>&1; then
            echo "  Проверка: эта ветка содержит $TARGET — работать можно."
        else
            echo "  ВНИМАНИЕ: эта ветка НЕ содержит $TARGET. Прежде чем что-то править,"
            echo "  переключитесь: git checkout -B "$TARGET" origin/"$TARGET""
        fi
    fi
fi
