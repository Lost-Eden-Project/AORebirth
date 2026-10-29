<?php
// Runs the actual endpoint through an owned PHP loopback HTTP fixture, with only
// isolated temporary claim roots. No AO engine, database, or client is contacted.
$endpoint = realpath(__DIR__ . '/../../tools-temp/icc-rk-local-web/daily/claim.php');
if ($endpoint === false) throw new RuntimeException('Daily reward endpoint was not found.');
$temporaryRoot = sys_get_temp_dir() . DIRECTORY_SEPARATOR . 'aorebirth-daily-api-' . bin2hex(random_bytes(12));
if (!mkdir($temporaryRoot, 0700)) throw new RuntimeException('Could not create isolated test data.');
$roots = array($temporaryRoot . DIRECTORY_SEPARATOR . 'one', $temporaryRoot . DIRECTORY_SEPARATOR . 'two');
foreach ($roots as $root) mkdir($root, 0700);
$fixtureAccount = 'fixture-' . bin2hex(random_bytes(12));
foreach ($roots as $root) file_put_contents($root . DIRECTORY_SEPARATOR . 'board-account-' . $fixtureAccount . '.json', json_encode(array('accountKey' => $fixtureAccount, 'taken' => array(), 'lastGrantedUtc' => '', 'lastClaimUtc' => '')));

function check($condition, $message) {
    if (!$condition) throw new RuntimeException($message);
}

function invoke_endpoint($method, $query = array(), $body = array(), $selectedRoots = null, $headers = array()) {
    global $endpoint, $roots, $fixtureAccount;
    $payload = $method === 'POST' ? json_encode($body) : '';
    $claimsRoots = implode(PATH_SEPARATOR, $selectedRoots === null ? $roots : $selectedRoots);
    $reservation = stream_socket_server('tcp://127.0.0.1:0', $socketError, $socketMessage);
    check($reservation !== false, 'Could not reserve an isolated loopback fixture address.');
    $address = stream_socket_get_name($reservation, false);
    fclose($reservation);
    $pipes = array();
    $previousRoots = getenv('AO_REBIRTH_DAILY_LOGIN_CLAIMS_ROOTS');
    putenv('AO_REBIRTH_DAILY_LOGIN_CLAIMS_ROOTS=' . $claimsRoots);
    try {
        $process = proc_open(array(PHP_BINARY, '-n', '-S', $address, '-t', dirname($endpoint)), array(0 => array('pipe', 'r'), 1 => array('pipe', 'w'), 2 => array('pipe', 'w')), $pipes, dirname($endpoint), null);
    } finally {
        putenv($previousRoots === false ? 'AO_REBIRTH_DAILY_LOGIN_CLAIMS_ROOTS' : 'AO_REBIRTH_DAILY_LOGIN_CLAIMS_ROOTS=' . $previousRoots);
    }
    if (!is_resource($process)) throw new RuntimeException('Could not run the owned PHP HTTP fixture.');
    fclose($pipes[0]);
    $response = false;
    $failure = '';
    try {
        $deadline = microtime(true) + 5;
        do {
            $connection = @stream_socket_client('tcp://' . $address, $socketError, $socketMessage, 0.1);
            if ($connection !== false) {
                fclose($connection);
                break;
            }
            $status = proc_get_status($process);
            check($status['running'], 'Owned PHP fixture exited before readiness.');
            usleep(20000);
        } while (microtime(true) < $deadline);
        check($connection !== false, 'Owned PHP fixture exceeded its five-second readiness timeout.');
        if ($method === 'POST') {
            // Prove the configured temporary root reached the real endpoint
            // before any POST can write. Never fall back to actual claim data.
            $proof = @file_get_contents('http://' . $address . '/claim.php?accountKey=' . $fixtureAccount);
            $proof = $proof === false ? null : json_decode($proof, true);
            check(is_array($proof) && isset($proof['accountKey']) && $proof['accountKey'] === $fixtureAccount, 'HTTP fixture did not select the isolated claim roots; POST was not sent.');
        }
        $context = stream_context_create(array('http' => array(
            'method' => $method, 'header' => "Content-Type: application/json\r\nConnection: close\r\n" . implode("\r\n", $headers) . "\r\n",
            'content' => $payload, 'ignore_errors' => true, 'timeout' => 5
        )));
        $response = @file_get_contents('http://' . $address . '/claim.php?' . http_build_query($query), false, $context);
    } catch (Throwable $error) {
        $failure = $error->getMessage();
    } finally {
        $status = proc_get_status($process);
        if ($status['running']) proc_terminate($process);
        $stdout = stream_get_contents($pipes[1]);
        $stderr = stream_get_contents($pipes[2]);
        fclose($pipes[1]);
        fclose($pipes[2]);
        proc_close($process);
    }
    check($failure === '', $failure . ' STDERR: ' . substr($stderr, -2000));
    $result = $response === false ? null : json_decode($response, true);
    check(is_array($result), 'Endpoint returned invalid JSON (' . $method . '). RESPONSE: ' . strval($response) . ' STDERR: ' . substr($stderr, -2000));
    return $result;
}

function save_fixture($root, $name, $value) {
    check(file_put_contents($root . DIRECTORY_SEPARATOR . $name, json_encode($value)) !== false, 'Could not write a test fixture.');
}

function test_case($name, $run) {
    $run();
    echo 'PASS ' . $name . PHP_EOL;
}

test_case('GET without hints does not create directories or attribute the globally last-active account', function () {
    global $roots, $temporaryRoot;
    save_fixture($roots[0], 'last-active-account.json', array('accountKey' => 'unrelated'));
    $absent = $temporaryRoot . DIRECTORY_SEPARATOR . 'absent';
    $result = invoke_endpoint('GET', array(), array(), array($absent));
    check($result['hasIdentity'] === false && $result['taken'] === array(), 'Anonymous GET inherited an account.');
    check(!is_dir($absent), 'GET created a claim directory.');
});

test_case('Native character headers bootstrap only the matching character and queue no grant', function () {
    global $roots, $fixtureAccount;
    $headers = array('X-Anarchy-CharacterID: 606');
    $board = invoke_endpoint('GET', array('accountKey' => $fixtureAccount), array(), null, $headers);
    check($board['characterId'] === 606 && $board['nativeCharacterHint'] === true && $board['hasIdentity'] === true, 'Native character routing was not returned.');
    check($board['accountKey'] === '' && $board['taken'] === array(), 'An unknown character inherited an unrelated account board.');
    $queued = invoke_endpoint('POST', array(), array('claimToken' => 'ctest-native-header'), null, $headers);
    check($queued['armed'] === true && $queued['committed'] === false && $queued['characterId'] === 606, 'Native hint did not queue its exact character.');
    foreach ($roots as $root) {
        $request = json_decode(file_get_contents($root . DIRECTORY_SEPARATOR . 'request-ctest-native-header.json'), true);
        check($request['CharacterId'] === 606 && $request['AccountKey'] === '', 'Native hint became an account identity.');
        check(!is_file($root . DIRECTORY_SEPARATOR . 'result-ctest-native-header.json'), 'HTTP produced a grant receipt.');
    }
});

test_case('Native headers reject invalid and conflicting routes without changing saved requests', function () {
    global $roots;
    foreach (array('0', '-1', '2147483648', '7x', '7,8') as $invalid) {
        $response = invoke_endpoint('GET', array(), array(), null, array('X-Anarchy-CharacterID: ' . $invalid));
        check($response['ok'] === false, 'Invalid native character header was accepted: ' . $invalid);
    }
    $maximum = invoke_endpoint('GET', array(), array(), null, array('X-Anarchy-CharacterID: 2147483647'));
    check($maximum['characterId'] === 2147483647, 'The native signed character-ID boundary was rejected.');
    $before = file_get_contents($roots[0] . DIRECTORY_SEPARATOR . 'request-ctest-native-header.json');
    $conflict = invoke_endpoint('POST', array(), array('claimToken' => 'ctest-native-header', 'characterId' => 607), null, array('X-Anarchy-CharacterID: 606'));
    check($conflict['ok'] === false && $conflict['armed'] === false, 'Conflicting body/header character routes were accepted.');
    check(invoke_endpoint('GET', array('characterId' => 607), array(), null, array('X-Anarchy-CharacterID: 606'))['ok'] === false, 'Conflicting query/header routes were accepted.');
    check(invoke_endpoint('POST', array('characterId' => 607), array('claimToken' => 'ctest-native-header', 'characterId' => 606), null, array('X-Anarchy-CharacterID: 606'))['ok'] === false, 'A conflicting query was hidden by the body route.');
    foreach ($roots as $root) check(file_get_contents($root . DIRECTORY_SEPARATOR . 'request-ctest-native-header.json') === $before, 'A conflict changed the original request.');
});

test_case('POST only queues an immutable token request and never writes progress or a grant result', function () {
    global $roots;
    $body = array('claimToken' => 'ctest-first', 'characterId' => 101, 'accountKey' => 'first');
    $result = invoke_endpoint('POST', array(), $body);
    check(isset($result['armed']) && $result['armed'] === true && $result['committed'] === false, 'POST reported a grant or failed to acknowledge a durable request: ' . json_encode($result));
    $original = file_get_contents($roots[0] . DIRECTORY_SEPARATOR . 'request-ctest-first.json');
    foreach ($roots as $root) {
        check(file_get_contents($root . DIRECTORY_SEPARATOR . 'request-ctest-first.json') === $original, 'Request roots differ.');
        check(!is_file($root . DIRECTORY_SEPARATOR . 'account-first.json'), 'HTTP wrote account progress.');
        check(!is_file($root . DIRECTORY_SEPARATOR . 'result-ctest-first.json'), 'HTTP wrote a grant result.');
        check(!is_file($root . DIRECTORY_SEPARATOR . 'pending-latest.json'), 'HTTP wrote an unsafe global queue.');
    }
    invoke_endpoint('POST', array(), $body);
    check(file_get_contents($roots[0] . DIRECTORY_SEPARATOR . 'request-ctest-first.json') === $original, 'Retry changed the original request.');
    $body['characterId'] = 202;
    $conflict = invoke_endpoint('POST', array(), $body);
    check($conflict['ok'] === false, 'Conflicting token ownership was accepted.');
    check(file_get_contents($roots[0] . DIRECTORY_SEPARATOR . 'request-ctest-first.json') === $original, 'Conflict discarded the original request.');
});

test_case('GET distinguishes an absent request, a durable pending request, and a committed server result', function () {
    global $roots;
    $missing = invoke_endpoint('GET', array('token' => 'ctest-absent'));
    check($missing['pending'] === true && $missing['armed'] === false, 'Absent token was reported as armed.');
    $pending = invoke_endpoint('GET', array('token' => 'ctest-first'));
    check($pending['pending'] === true && $pending['armed'] === true && $pending['ok'] === null, 'Queued request was reported as delivered.');
    $receipt = array('ok' => true, 'committed' => true, 'claimToken' => 'ctest-first', 'accountKey' => 'first', 'taken' => array(1));
    foreach ($roots as $root) save_fixture($root, 'result-ctest-first.json', $receipt);
    check(invoke_endpoint('GET', array('token' => 'ctest-first')) === $receipt, 'GET did not return the authoritative committed result.');
});

test_case('A historical successful result resolves without inventing atomic commitment or rewriting its original', function () {
    global $roots;
    $legacy = array('ok' => true, 'day' => 1, 'accountKey' => 'first', 'taken' => array(1));
    foreach ($roots as $root) save_fixture($root, 'result-ctest-legacy.json', $legacy);
    $original = file_get_contents($roots[0] . DIRECTORY_SEPARATOR . 'result-ctest-legacy.json');
    $result = invoke_endpoint('GET', array('token' => 'ctest-legacy'));
    check($result['legacyResolved'] === true && $result['terminal'] === true && $result['committed'] === false, 'Historical success was not distinguished from a new atomic grant.');
    foreach ($roots as $root) check(file_get_contents($root . DIRECTORY_SEPARATOR . 'result-ctest-legacy.json') === $original, 'Historical result was rewritten.');
});

test_case('Existing progress remains byte-identical and board reads do not reset cycles or dates', function () {
    global $roots;
    $progress = array('Taken' => array(1, 2), 'LastClaimUtc' => '2020-01-01', 'LastGrantedUtc' => '2020-01-01', 'CycleCompletedOn' => '2020-01-01');
    foreach ($roots as $root) save_fixture($root, 'account-progress.json', $progress);
    $original = file_get_contents($roots[0] . DIRECTORY_SEPARATOR . 'account-progress.json');
    $result = invoke_endpoint('GET', array('accountKey' => 'progress'));
    check($result['taken'] === array(1, 2) && $result['lastGrantedUtc'] === '2020-01-01', 'Read-only historical progress was reset or altered.');
    check(file_get_contents($roots[0] . DIRECTORY_SEPARATOR . 'account-progress.json') === $original, 'GET modified original progress.');
});

test_case('Completed cycles unlock tomorrow UTC through a read-only board projection', function () {
    global $roots;
    $yesterday = gmdate('Y-m-d', time() - 86400);
    $board = array('accountKey' => 'completed', 'taken' => range(1, 28), 'lastGrantedUtc' => $yesterday, 'lastClaimUtc' => $yesterday, 'cycleCompletedOn' => $yesterday, 'claimedToday' => true);
    $legacy = array('Taken' => range(1, 28), 'LastClaimUtc' => $yesterday, 'LastGrantedUtc' => '', 'CycleCompletedOn' => '');
    foreach ($roots as $root) {
        save_fixture($root, 'board-account-completed.json', $board);
        save_fixture($root, 'account-legacy-completed.json', $legacy);
    }
    $boardOriginal = file_get_contents($roots[0] . DIRECTORY_SEPARATOR . 'board-account-completed.json');
    $legacyOriginal = file_get_contents($roots[0] . DIRECTORY_SEPARATOR . 'account-legacy-completed.json');
    foreach (array('completed', 'legacy-completed') as $account) {
        $result = invoke_endpoint('GET', array('accountKey' => $account));
        check($result['taken'] === array() && $result['nextDay'] === 1 && $result['claimedCount'] === 0, 'A completed prior-day cycle did not unlock day one.');
        check($result['lastGrantedUtc'] === '' && $result['lastClaimUtc'] === '' && $result['cycleCompletedOn'] === '' && $result['claimedToday'] === false, 'Completed-cycle projection retained a stale daily lock.');
    }
    foreach ($roots as $root) {
        check(file_get_contents($root . DIRECTORY_SEPARATOR . 'board-account-completed.json') === $boardOriginal, 'Projection rewrote the published board.');
        check(file_get_contents($root . DIRECTORY_SEPARATOR . 'account-legacy-completed.json') === $legacyOriginal, 'Projection rewrote historical progress.');
    }
});

test_case('Incomplete cycles carry across months and daily availability derives from the UTC grant date', function () {
    global $roots;
    $board = array('accountKey' => 'month-carry', 'month' => '2020-01', 'taken' => array(1, 2), 'lastGrantedUtc' => '2020-01-31', 'lastClaimUtc' => '2020-01-31', 'cycleCompletedOn' => '', 'claimedToday' => true);
    $today = array('accountKey' => 'today', 'taken' => array(1), 'lastGrantedUtc' => gmdate('Y-m-d'), 'lastClaimUtc' => gmdate('Y-m-d'), 'claimedToday' => false);
    foreach ($roots as $root) {
        save_fixture($root, 'board-account-month-carry.json', $board);
        save_fixture($root, 'board-account-today.json', $today);
    }
    $original = file_get_contents($roots[0] . DIRECTORY_SEPARATOR . 'board-account-month-carry.json');
    $result = invoke_endpoint('GET', array('accountKey' => 'month-carry'));
    check($result['taken'] === array(1, 2) && $result['nextDay'] === 3 && $result['claimedCount'] === 2, 'Month change discarded an incomplete cycle.');
    check($result['claimedToday'] === false && $result['month'] === gmdate('Y-m'), 'Daily availability used stale board metadata.');
    check(invoke_endpoint('GET', array('accountKey' => 'today'))['claimedToday'] === true, 'A current UTC grant did not retain its daily lock.');
    foreach ($roots as $root) check(file_get_contents($root . DIRECTORY_SEPARATOR . 'board-account-month-carry.json') === $original, 'Month projection rewrote account progress.');
});

test_case('Divergent progress roots fail closed without rewriting either original', function () {
    global $roots;
    save_fixture($roots[0], 'account-conflict.json', array('Taken' => array(1)));
    save_fixture($roots[1], 'account-conflict.json', array('Taken' => array(1, 2)));
    $before = array_map(function ($root) { return file_get_contents($root . DIRECTORY_SEPARATOR . 'account-conflict.json'); }, $roots);
    $result = invoke_endpoint('GET', array('accountKey' => 'conflict'));
    check($result['ok'] === false, 'Different progress roots were silently selected.');
    foreach ($roots as $index => $root) check(file_get_contents($root . DIRECTORY_SEPARATOR . 'account-conflict.json') === $before[$index], 'A conflict rewrote original progress.');
});

test_case('Explicit unknown character hints can queue, but anonymous POST cannot use last-active fallback', function () {
    global $roots;
    $board = invoke_endpoint('GET', array('characterId' => 303));
    check($board['hasIdentity'] === true && $board['taken'] === array(), 'A routing hint was treated as existing progress.');
    $missing = invoke_endpoint('POST', array(), array('claimToken' => 'ctest-unbound'));
    check($missing['ok'] === false && $missing['armed'] === false, 'Anonymous request inherited the last-active account.');
    foreach ($roots as $root) check(!is_file($root . DIRECTORY_SEPARATOR . 'request-ctest-unbound.json'), 'Anonymous queue file was created.');
});

test_case('Published empty account boards remain empty and invalid tokens cannot traverse directories', function () {
    global $roots;
    $board = array('accountKey' => 'empty', 'taken' => array(), 'lastGrantedUtc' => '', 'lastClaimUtc' => '', 'claimedCount' => 0);
    foreach ($roots as $root) save_fixture($root, 'board-character-404.json', $board);
    $result = invoke_endpoint('GET', array('characterId' => 404, 'accountKey' => 'unrelated'));
    check($result['accountKey'] === 'empty' && $result['taken'] === array() && $result['lastGrantedUtc'] === '', 'Empty or character-scoped board was replaced by a hint.');
    check(invoke_endpoint('GET', array('token' => '../account-empty'))['ok'] === false, 'Traversal token was accepted.');
});

test_case('Tokens match the native 64-character limit and account hints use portable filenames', function () {
    global $roots;
    $tooLong = str_repeat('x', 65);
    $invalid = invoke_endpoint('POST', array(), array('claimToken' => $tooLong, 'characterId' => 505));
    check($invalid['ok'] === false && $invalid['armed'] === false, 'A token too long for the native ledger was queued.');
    foreach ($roots as $root) check(!is_file($root . DIRECTORY_SEPARATOR . 'request-' . $tooLong . '.json'), 'An unreadable native request was published.');
    $normalized = invoke_endpoint('POST', array(), array('claimToken' => 'ctest-portable', 'accountKey' => '  Route:Hint  '));
    check($normalized['armed'] === true && $normalized['accountKey'] === 'route_hint', 'Account routing hint was not trimmed and normalized to the native filename rules.');
    foreach ($roots as $root) {
        $request = json_decode(file_get_contents($root . DIRECTORY_SEPARATOR . 'request-ctest-portable.json'), true);
        check($request['AccountKey'] === 'route_hint', 'Queue account hint differs from the native safe key.');
    }
});

// All files here were created by this test under its exclusive random directory.
$canonical = realpath($temporaryRoot);
check($canonical !== false && strpos($canonical, realpath(sys_get_temp_dir()) . DIRECTORY_SEPARATOR . 'aorebirth-daily-api-') === 0, 'Unexpected cleanup root.');
$iterator = new RecursiveIteratorIterator(new RecursiveDirectoryIterator($canonical, FilesystemIterator::SKIP_DOTS), RecursiveIteratorIterator::CHILD_FIRST);
foreach ($iterator as $entry) {
    $path = $entry->getPathname();
    check(strpos($path, $canonical . DIRECTORY_SEPARATOR) === 0, 'Cleanup path escaped isolated test data.');
    if ($entry->isDir() && !$entry->isLink()) rmdir($path);
    else unlink($path);
}
rmdir($canonical);
