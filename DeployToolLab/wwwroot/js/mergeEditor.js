
window.mergeEditor = {
    // Sync right textarea scroll to left panel scroll and vice versa
    syncScroll: function (leftId, rightId) {
        const left = document.getElementById(leftId);
        const right = document.getElementById(rightId);
        if (!left || !right) return;

        let syncing = false;

        left.addEventListener('scroll', () => {
            if (syncing) return;
            syncing = true;
            right.scrollTop = left.scrollTop;
            syncing = false;
        });

        right.addEventListener('scroll', () => {
            if (syncing) return;
            syncing = true;
            left.scrollTop = right.scrollTop;
            syncing = false;
        });
    },

    // Focus the textarea and place cursor at end
    focus: function (id) {
        const el = document.getElementById(id);
        if (el) { el.focus(); }
    }
};
