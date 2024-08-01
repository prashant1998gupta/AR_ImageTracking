// webgl-sharing.js
function shareText(text, url) {
    if (navigator.share) {
        navigator.share({
            title: 'Check this out!',
            text: text,
            url: url
        }).then(() => {
            console.log('Thanks for sharing!');
        }).catch((error) => {
            console.log('Error sharing:', error);
        });
    } else {
        // Fallback for browsers that do not support the Web Share API
        alert('Your browser does not support the Web Share API.');
    }
}
