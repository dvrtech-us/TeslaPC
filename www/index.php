<?php
include '../common.php';
// get the server address the user is coming from
$server = $_SERVER['SERVER_NAME'];

?>
<img style="max-width: 100%" src="http://<?php echo $server ?>:9000" />
<!-- <audio controls autoplay src="http://<?php echo $server ?>:63859/stream/swyh.mp3"></audio> -->
<!-- Include the WebSocket JavaScript library -->
<script>
    // Create a WebSocket connection
    var socket = new WebSocket('ws://<?php echo $server ?>:8081/ws/');

    // Get the image element
    var img = document.querySelector('img');

    // Add an event listener for the 'click' event
    img.addEventListener('click', function(event) {
        // Get the click coordinates relative to the image
        var rect = img.getBoundingClientRect();
        var x = event.clientX - rect.left;
        var y = event.clientY - rect.top;
        //take into account the image size vs the actual size
        x = x * img.naturalWidth / img.width;
        y = y * img.naturalHeight / img.height;
        //convert to integer
        x = parseInt(x);
        y = parseInt(y);

        // Send these coordinates over the WebSocket connection
        socket.send(JSON.stringify({
            Type: 'click',
            X: x,
            Y: y
        }));
    });
</script>