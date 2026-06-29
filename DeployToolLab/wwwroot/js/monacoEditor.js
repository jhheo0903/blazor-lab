globalThis.monacoEditor = (() => {
    const _inst = {};

    const ensureMonaco = () => new Promise(resolve => {
        if (globalThis.monaco) { resolve(); return; }
        require(['vs/editor/editor.main'], resolve);
    });

    function syncLineNumberWidth(i) {
        const maxLines = Math.max(i.origModel.getLineCount(), i.modModel.getLineCount());
        const minChars = String(maxLines).length;
        const opts = { lineNumbersMinChars: minChars, glyphMargin: false };
        i.editor.getOriginalEditor().updateOptions(opts);
        i.editor.getModifiedEditor().updateOptions(opts);
    }

    // setValue/updateModel 같은 프로그래매틱 변경 시 onDidChangeContent 무시
    function setProgrammatic(i, modifiedText) {
        i.programmatic = true;
        i.modModel.setValue(modifiedText ?? '');
        i.programmatic = false;
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
            const inst = { editor, origModel, modModel, programmatic: false };
            _inst[containerId] = inst;

            syncLineNumberWidth(inst);

            // 사용자 편집만 감지 — diff editor의 modified editor 인스턴스에 직접 등록
            let dirtyTimer;
            editor.getModifiedEditor().onDidChangeModelContent(() => {
                syncLineNumberWidth(inst);
                if (inst.programmatic) return;
                clearTimeout(dirtyTimer);
                dirtyTimer = setTimeout(() => {
                    dotnetRef.invokeMethodAsync('OnMonacoDirty')
                        .catch(e => console.error('[monacoEditor] OnMonacoDirty 실패', e));
                }, 300);
            });
        },

        updateModel(containerId, originalText, modifiedText) {
            const i = _inst[containerId];
            if (!i) return;
            i.origModel.setValue(originalText ?? '');
            setProgrammatic(i, modifiedText);
            syncLineNumberWidth(i);
        },

        setValue(containerId, text) {
            const i = _inst[containerId];
            if (!i) return;
            setProgrammatic(i, text);
            syncLineNumberWidth(i);
        },

        getValue(containerId) {
            const i = _inst[containerId];
            if (!i) return '';
            return i.modModel.getValue();
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
