// 파일 트리 영역 너비 조절 — .tree-splitter 바로 앞 요소(.tree-pane)의 너비를 드래그로 변경
globalThis.treeSplitter = (() => {
    const STORAGE_KEY = 'deploytool.treePaneWidth';
    const DEFAULT_WIDTH = 280;
    const MIN_WIDTH = 200;
    const MAX_RATIO = 0.7; // 컨테이너 너비 대비 최대 비율

    function clamp(width, pane) {
        const containerWidth = pane.parentElement?.clientWidth || window.innerWidth;
        return Math.round(Math.min(Math.max(width, MIN_WIDTH), containerWidth * MAX_RATIO));
    }

    function setWidth(pane, width) {
        pane.style.width = `${clamp(width, pane)}px`;
    }

    function save(width) {
        try { localStorage.setItem(STORAGE_KEY, String(width)); } catch { /* 저장 불가 환경 무시 */ }
    }

    function load() {
        try { return parseInt(localStorage.getItem(STORAGE_KEY), 10) || DEFAULT_WIDTH; }
        catch { return DEFAULT_WIDTH; }
    }

    function restore() {
        const width = load();
        document.querySelectorAll('.tree-splitter').forEach(s => {
            if (s.previousElementSibling) setWidth(s.previousElementSibling, width);
        });
    }

    document.addEventListener('pointerdown', e => {
        const splitter = e.target.closest?.('.tree-splitter');
        const pane = splitter?.previousElementSibling;
        if (!pane || e.button !== 0) return;

        e.preventDefault();
        const startX = e.clientX;
        const startWidth = pane.getBoundingClientRect().width;
        splitter.setPointerCapture(e.pointerId);
        splitter.classList.add('dragging');
        document.body.classList.add('is-resizing');

        const onMove = ev => setWidth(pane, startWidth + ev.clientX - startX);
        const onUp = () => {
            splitter.removeEventListener('pointermove', onMove);
            splitter.removeEventListener('pointerup', onUp);
            splitter.removeEventListener('pointercancel', onUp);
            splitter.classList.remove('dragging');
            document.body.classList.remove('is-resizing');
            save(pane.getBoundingClientRect().width);
        };

        splitter.addEventListener('pointermove', onMove);
        splitter.addEventListener('pointerup', onUp);
        splitter.addEventListener('pointercancel', onUp);
    });

    // 더블클릭 시 기본 너비로 복원
    document.addEventListener('dblclick', e => {
        const pane = e.target.closest?.('.tree-splitter')?.previousElementSibling;
        if (!pane) return;
        setWidth(pane, DEFAULT_WIDTH);
        save(DEFAULT_WIDTH);
    });

    return { restore };
})();
