window.monacoEditor = (() => {
    const _inst = {};

    const ensureMonaco = () => new Promise(resolve => {
        if (window.monaco) { resolve(); return; }
        require(['vs/editor/editor.main'], resolve);
    });

    // 두 모델의 라인 수 중 큰 쪽의 자릿수를 계산해 양쪽 에디터에 동일하게 적용
    function syncLineNumberWidth(i) {
        const maxLines = Math.max(i.origModel.getLineCount(), i.modModel.getLineCount());
        const minChars = String(maxLines).length;
        const opts = { lineNumbersMinChars: minChars, glyphMargin: false };
        i.editor.getOriginalEditor().updateOptions(opts);
        i.editor.getModifiedEditor().updateOptions(opts);
    }

    return {
        async createDiff(containerId, originalText, modifiedText, language, isReadOnly, dotnetRef) {
            await ensureMonaco();

            const el = document.getElementById(containerId);
            if (!el) return;

            if (_inst[containerId]) {
                _inst[containerId].editor.dispose();
                delete _inst[containerId];
            }

            const origModel = monaco.editor.createModel(originalText ?? '', language ?? 'plaintext');
            const modModel  = monaco.editor.createModel(modifiedText  ?? '', language ?? 'plaintext');

            const editor = monaco.editor.createDiffEditor(el, {
                originalEditable: false,
                readOnly: isReadOnly,
                theme: 'vs-dark',
                renderSideBySide: true,
                automaticLayout: true,
                minimap: { enabled: false },
                scrollBeyondLastLine: false,
                fontSize: 12,
                fontFamily: "'JetBrains Mono', Consolas, 'Courier New', monospace",
                lineHeight: 20,
                wordWrap: 'off',
                ignoreTrimWhitespace: false,
                renderOverviewRuler: false,
                renderIndicators: true,
                glyphMargin: false,
                renderMarginRevertIcon: false,
                renderGutterMenu: false,
            });

            editor.setModel({ original: origModel, modified: modModel });
            const inst = { editor, origModel, modModel };
            _inst[containerId] = inst;

            syncLineNumberWidth(inst);

            // 편집으로 라인 수가 변하면 너비 재동기화
            modModel.onDidChangeContent(() => {
                syncLineNumberWidth(inst);
            });

            let timer;
            modModel.onDidChangeContent(() => {
                clearTimeout(timer);
                timer = setTimeout(() => {
                    dotnetRef.invokeMethodAsync('OnMonacoChange', modModel.getValue());
                }, 250);
            });
        },

        updateModel(containerId, originalText, modifiedText) {
            const i = _inst[containerId];
            if (!i) return;
            i.origModel.setValue(originalText ?? '');
            i.modModel.setValue(modifiedText  ?? '');
            syncLineNumberWidth(i);
        },

        setValue(containerId, text) {
            const i = _inst[containerId];
            if (!i) return;
            i.modModel.setValue(text ?? '');
            syncLineNumberWidth(i);
        },

        dispose(containerId) {
            const i = _inst[containerId];
            if (!i) return;
            i.editor.dispose();
            i.origModel.dispose();
            i.modModel.dispose();
            delete _inst[containerId];
        }
    };
})();
