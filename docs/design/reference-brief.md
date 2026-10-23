# Референс parallexity 1.0

Файл: `parallexity-1.0-reference.png`. Создан встроенным image_gen; затем тем же инструментом индикаторы связи приведены к монохромной палитре. Референс иллюстративный: отображение Кинопоиска не подтверждает совместимость реального плеера.

## Задание генерации

Два экрана телефонного пульта Windows: «Компьютер» и «Медиа». Apple-inspired restraint, graphite dark mode, grouped inset lists. No circular buttons, including transport, close, shortcuts and navigation. Palette #111113 / #1c1c1f / #28282c / #f5f5f7 / #aeaeb8. Rounded rectangles: cards 20 px, controls 12 px, icon containers 10 px. Page insets 20 px, card padding 16 px, section gaps 24 px, touch targets 48 px. System sans typography 32 / 20 / 16 / 13 px.

«Компьютер»: статус связи; избранное 2×2 (Chrome, Discord, Музыка, Добавить); сгруппированный список открытых окон с названиями и заголовками; пояснение о показе окна на мониторе; строки «Погасить экран» и «Таймер». «Медиа»: выбор источника; название серии; три прямоугольные кнопки транспорта; пропуск заставки; громкость видео и отдельное отключение звука; действия выбора вкладки и показа на ПК; свёрнутая диагностика. Нижняя навигация на обоих экранах: ПК / Медиа / Звук / Управление. Без цветных логотипов, декоративных иллюстраций и свечения. Чистый читаемый русский текст, без наложения элементов.

Финальная правка image_gen: recolor both green connection status dots to neutral off-white #f5f5f7; preserve all layout, text, spacing, icons, shapes and every other detail.

Нормативный источник размеров и поведения — `../design-system.md`, цветов и CSS-переменных — `../../web/design-tokens.css`. Генерация может приблизительно передавать размеры и фактуру; реализация следует токенам.
