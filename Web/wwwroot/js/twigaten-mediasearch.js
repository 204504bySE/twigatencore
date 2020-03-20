﻿'use strict';
(function () {
    const mediaDrop = document.getElementById('twigaten-media-drop');
    const mediaInput = document.getElementById('twigaten-media-input');
    const mediaForm = document.getElementById('twigaten-media-form');

    mediaDrop.addEventListener('dragover', (event) => {
        event.preventDefault();
        event.stopPropagation();
        event.currentTarget.classList.toggle('dragging', true);
    });
    mediaDrop.addEventListener('dragenter', (event) => {
        event.preventDefault();
        event.stopPropagation();
        event.currentTarget.classList.toggle('dragging', true);
    });
    mediaDrop.addEventListener('dragleave', (event) => {
        event.preventDefault();
        event.stopPropagation();
        event.currentTarget.classList.remove('dragging');
    });
    //ドロップしたらformを操作してPOST(雑)
    mediaDrop.addEventListener('drop', (event) => {
        event.preventDefault();
        event.stopPropagation();
        event.currentTarget.classList.remove('dragging');
        if (event.dataTransfer.files && 0 < event.dataTransfer.files.length) {
            const file = event.dataTransfer.files[0];
            if (file.type.startsWith('image/')) {
                event.currentTarget.classList.toggle('dropped', true);
                mediaInput.files = event.dataTransfer.files;
                mediaForm.submit();
            }
        }
    });
    //ファイルを選択したときもPOST
    mediaInput.addEventListener('change', () => {
        mediaDrop.classList.toggle('dropped', true);
        mediaForm.submit();
    });
})();